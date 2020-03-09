using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Azure.Batch;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Blob;
using Microsoft.WindowsAzure.Storage.Shared.Protocol;
using Pulumi;
using Pulumi.Azure.AppInsights;
using Pulumi.Azure.AppService;
using Pulumi.Azure.AppService.Inputs;
using Pulumi.Azure.Core;
using Pulumi.Azure.Sql;
using Pulumi.Azure.Storage;

class Program
{
    static Task<int> Main()
    {
        return Deployment.RunAsync(() =>
        {
            // Create an Azure Resource Group
            var resourceGroup = new ResourceGroup("resourceGroup");

            // Create an Azure App Insights
            var appInsightsKey = CreateAzureAppInsights(resourceGroup);

            // Create an Azure Static Websites (inside Storage Account)
            var frontEndpoint = CreateAzureStaticWebsites(resourceGroup);

            // Create and Azure SQL Server
            var sqlConnectionString = CreateAzureSqlInstance(resourceGroup, out SqlServer sqlServer, out Database sqlDatabase);

            // Create an Azure Functions Account (using Service Plan and backed by Storage Account)
            var functionApp = CreateAzureFunctionApp(resourceGroup, sqlConnectionString, appInsightsKey);
            SetFirewallRulesToAccessSqlServer(resourceGroup, functionApp, sqlServer); // Give access from Functions -> SQL database

            // Export information of Azure resources created
            return new Dictionary<string, object?>
            {
                { "sqlServerConnectionString", sqlConnectionString },
                { "apiEndpoint", Output.Format($"https://{functionApp.DefaultHostname}/api") },
                { "frontEndpoint", frontEndpoint }
            };
        });
    }

    static Output<string> CreateAzureAppInsights(ResourceGroup resourceGroup)
    {
        var appInsights = new Insights("appinsights", new InsightsArgs
        {
            ResourceGroupName = resourceGroup.Name,
            ApplicationType = "web"
        });

        return appInsights.InstrumentationKey;
    }

    static Output<string?> CreateAzureStaticWebsites(ResourceGroup resourceGroup)
    {
        var frontendStorageAccount = new Account("frontendstorage", new AccountArgs
        {
            ResourceGroupName = resourceGroup.Name,
            AccountReplicationType = "LRS",
            EnableHttpsTrafficOnly = true,
            AccountTier = "Standard",
            AccountKind = "StorageV2",
            AccessTier = "Hot",
        });

        var frontEndpoint = Output.All<string>(frontendStorageAccount.Name, frontendStorageAccount.PrimaryBlobConnectionString).Apply(async x =>
        {
            string frontendStorageAccountName = x[0];
            string connectionString = x[1];

            await EnableStaticWebsite(connectionString);
            var websiteUrl = GetStaticWebsiteEndpoint(frontendStorageAccountName);
            await UploadFilesToStaticWebsite(connectionString);

            return websiteUrl;
        });

        return frontEndpoint;
    }
    static Task EnableStaticWebsite(string connectionString)
    {
        var sa = CloudStorageAccount.Parse(connectionString);

        var blobClient = sa.CreateCloudBlobClient();
        var blobServiceProperties = new ServiceProperties
        {
            StaticWebsite = new StaticWebsiteProperties
            {
                Enabled = true,
                IndexDocument = "index.html",
                ErrorDocument404Path = "index.html"
            }
        };

        return blobClient.SetServicePropertiesAsync(blobServiceProperties);
    }
    static string? GetStaticWebsiteEndpoint(string frontendStorageAccountName)
    {
        string getWebsiteEndpointCli = $"az storage account show --name {frontendStorageAccountName} --query primaryEndpoints.web";

        var processInfo = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
            Arguments = $"/K {getWebsiteEndpointCli}"
        };

        var process = Process.Start(processInfo);
        string? line = null;

        while (!process.StandardOutput.EndOfStream)
        {
            line = process.StandardOutput.ReadLine();

            if (!string.IsNullOrWhiteSpace(line))
            {
                line = line.Replace("\"", "");
                break;
            }
        }

        return line;
    }

    static Task UploadFilesToStaticWebsite(string connectionString)
    {
        var sa = CloudStorageAccount.Parse(connectionString);

        var blobClient = sa.CreateCloudBlobClient();

        string inputPath = Path.Combine(Environment.CurrentDirectory, "../../Front/build");
        var inputFilePaths = new List<string>(
            Directory.GetFileSystemEntries(inputPath, "*", SearchOption.AllDirectories)
        );

        return UploadFilesToContainerAsync(
            blobClient,
            "$web",
            inputFilePaths
        );
    }
    /// <summary>
    /// Uploads the specified resource files to a container.
    /// </summary>
    /// <param name="blobClient">A <see cref="CloudBlobClient"/>.</param>
    /// <param name="containerName">Name of the blob storage container to which the files are uploaded.</param>
    /// <param name="filePaths">A collection of paths of the files to be uploaded to the container.</param>
    /// <returns>A collection of <see cref="ResourceFile"/> objects.</returns>
    private static async Task<List<ResourceFile>> UploadFilesToContainerAsync(CloudBlobClient blobClient, string containerName, List<string> filePaths)
    {
        List<ResourceFile> resourceFiles = new List<ResourceFile>();

        foreach (string filePath in filePaths)
        {
            resourceFiles.Add(await UploadResourceFileToContainerAsync(blobClient, containerName, filePath));
        }

        return resourceFiles;
    }
    /// <summary>
    /// Uploads the specified file to the specified blob container.
    /// </summary>
    /// <param name="blobClient">A <see cref="CloudBlobClient"/>.</param>
    /// <param name="containerName">The name of the blob storage container to which the file should be uploaded.</param>
    /// <param name="filePath">The full path to the file to upload to Storage.</param>
    /// <returns>A ResourceFile object representing the file in blob storage.</returns>
    private static async Task<ResourceFile> UploadResourceFileToContainerAsync(CloudBlobClient blobClient, string containerName, string filePath)
    {
        Console.WriteLine("Uploading file {0} to container [{1}]...", filePath, containerName);

        string blobName = Path.GetFileName(filePath);

        var container = blobClient.GetContainerReference(containerName);
        var blobData = container.GetBlockBlobReference(blobName);
        await blobData.UploadFromFileAsync(filePath);

        // Set the expiry time and permissions for the blob shared access signature. In this case, no start time is specified,
        // so the shared access signature becomes valid immediately
        var sasConstraints = new SharedAccessBlobPolicy
        {
            SharedAccessExpiryTime = DateTime.UtcNow.AddHours(2),
            Permissions = SharedAccessBlobPermissions.Read
        };

        // Construct the SAS URL for blob
        string sasBlobToken = blobData.GetSharedAccessSignature(sasConstraints);
        string blobSasUri = string.Format("{0}{1}", blobData.Uri, sasBlobToken);

        return new ResourceFile(blobSasUri, blobName);
    }

    static Output<string> CreateAzureSqlInstance(ResourceGroup resourceGroup, out SqlServer sqlServer, out Database sqlDatabase)
    {
        sqlServer = new SqlServer("sqlserver", new SqlServerArgs
        {
            ResourceGroupName = resourceGroup.Name,
            AdministratorLogin = TodoAppConstants.SqlServerUsername,
            AdministratorLoginPassword = TodoAppConstants.SqlServerPassword,
            Version = "12.0"
        });
        sqlDatabase = new Database("sqldatabase", new DatabaseArgs
        {
            ResourceGroupName = resourceGroup.Name,
            ServerName = sqlServer.Name,
            MaxSizeBytes = 104857600.ToString(),
            RequestedServiceObjectiveName = "Basic",
            Edition = "Basic"
        });

        return Output.Format($"Server=tcp:{sqlServer.FullyQualifiedDomainName};initial catalog={sqlDatabase.Name};user ID={TodoAppConstants.SqlServerUsername};password={TodoAppConstants.SqlServerPassword};Min Pool Size=0;Max Pool Size=30;Persist Security Info=true;");
    }

    static FunctionApp CreateAzureFunctionApp(ResourceGroup resourceGroup, Output<string> sqlConnectionString, Output<string> appInsightsKey)
    {
        var appServicePlan = new Plan("asp", new PlanArgs
        {
            ResourceGroupName = resourceGroup.Name,
            Kind = "FunctionApp",
            Sku = new PlanSkuArgs
            {
                Tier = "Dynamic",
                Size = "Y1",
            },
        });

        var backendStorageAccount = new Account("backendstorage", new AccountArgs
        {
            ResourceGroupName = resourceGroup.Name,
            AccountReplicationType = "LRS",
            EnableHttpsTrafficOnly = true,
            AccountTier = "Standard",
            AccountKind = "StorageV2",
            AccessTier = "Hot",
        });

        var container = new Container("zips", new ContainerArgs
        {
            StorageAccountName = backendStorageAccount.Name,
            ContainerAccessType = "private",
        });
        var blob = new ZipBlob("zip", new ZipBlobArgs
        {
            StorageAccountName = backendStorageAccount.Name,
            StorageContainerName = container.Name,
            Type = "block",
            Content = new FileArchive("../../TodoFunctions/bin/Release/netcoreapp2.1/publish"),
        });
        var codeBlobUrl = SharedAccessSignature.SignedBlobReadUrl(blob, backendStorageAccount);

        return new FunctionApp("app", new FunctionAppArgs
        {
            ResourceGroupName = resourceGroup.Name,
            AppServicePlanId = appServicePlan.Id,
            AppSettings =
            {
                { "runtime", "dotnet" },
                { "WEBSITE_RUN_FROM_PACKAGE", codeBlobUrl },
                { "ConnectionString", sqlConnectionString },
                { "APPINSIGHTS_INSTRUMENTATIONKEY", appInsightsKey }
            },
            SiteConfig = new FunctionAppSiteConfigArgs
            {
                Cors = new FunctionAppSiteConfigCorsArgs
                {
                    AllowedOrigins = new List<string> { "*" }
                }
            },
            StorageConnectionString = backendStorageAccount.PrimaryConnectionString,
            Version = "~2"
        });
    }

    static void SetFirewallRulesToAccessSqlServer(ResourceGroup resourceGroup, FunctionApp functionApp, SqlServer sqlServer)
    {
        functionApp.OutboundIpAddresses.Apply(outboundIpAddresses =>
        {
            return outboundIpAddresses
                .Split(new[] { "," }, StringSplitOptions.RemoveEmptyEntries)
                .Select(ip =>
                {
                    return new FirewallRule($"fw{ip}", new FirewallRuleArgs
                    {
                        ResourceGroupName = resourceGroup.Name,
                        ServerName = sqlServer.Name,
                        StartIpAddress = ip,
                        EndIpAddress = ip
                    });
                })
                .ToList();
        });
    }
}
