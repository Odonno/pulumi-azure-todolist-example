using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.WindowsAzure.Storage;
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

            // Create and Azure SQL Server
            var sqlConnectionString = CreateAzureSqlInstance(resourceGroup, out SqlServer sqlServer, out Database sqlDatabase);

            // Create an Azure Functions Account (using Service Plan and backed by Storage Account)
            var functionApp = CreateAzureFunctionApp(resourceGroup, sqlConnectionString, appInsightsKey);
            SetFirewallRulesToAccessSqlServer(resourceGroup, functionApp, sqlServer); // Give access from Functions -> SQL database

            var apiEndpointOutput = Output.Format($"https://{functionApp.DefaultHostname}/api");

            // Create an Azure Static Websites (inside Storage Account)
            var frontEndpoint = CreateAzureStaticWebsites(resourceGroup, apiEndpointOutput);

            // Export information of Azure resources created
            return new Dictionary<string, object?>
            {
                { "sqlServerConnectionString", sqlConnectionString },
                { "apiEndpoint", apiEndpointOutput },
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

    static Output<string> CreateAzureStaticWebsites(ResourceGroup resourceGroup, Output<string> apiEndpointOutput)
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

        frontendStorageAccount.PrimaryBlobConnectionString.Apply(async connectionString =>
        {
            if (!Deployment.Instance.IsDryRun)
            {
                await EnableStaticWebsite(connectionString);
            }
        });

        apiEndpointOutput.Apply(apiEndpoint =>
        {
            if (!Deployment.Instance.IsDryRun)
            {
                ReplaceBackendUrlInStaticWebsite(apiEndpoint);
            }

            // Upload files to static websites
            string folderPath = Path.GetFullPath(Path.Combine(System.Environment.CurrentDirectory, @"..\..\Front\build"));
            var files = GetFilesInFolder(folderPath);

            return files
                .Select(file =>
                {
                    string blobName = Path.GetFullPath(file).Substring(folderPath.Length + 1);

                    if (!new FileExtensionContentTypeProvider().TryGetContentType(file, out string contentType))
                    {
                        contentType = "application/octet-stream";
                    }

                    return new Blob(blobName, new BlobArgs
                    {
                        Name = blobName,
                        StorageAccountName = frontendStorageAccount.Name,
                        StorageContainerName = "$web",
                        Type = "Block",
                        Source = new FileAsset(file),
                        ContentType = contentType,
                    });
                })
                .ToList();
        });

        return frontendStorageAccount.PrimaryWebEndpoint;
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
    static void ReplaceBackendUrlInStaticWebsite(string apiEndpoint)
    {
        string folderPath = Path.GetFullPath(Path.Combine(System.Environment.CurrentDirectory, @"..\..\Front\build"));
        var files = GetFilesInFolder(folderPath);
        var mainJsFiles = files.Where(f => f.EndsWith(".js") && f.Contains("main."));

        foreach (var mainJsFile in mainJsFiles)
        {
            File.WriteAllText(
                mainJsFile,
                File.ReadAllText(mainJsFile).Replace("#{REACT_APP_TODO_API_ENDPOINT}", apiEndpoint)
            );
        }
    }
    static IEnumerable<string> GetFilesInFolder(string directory)
    {
        return Directory.EnumerateFiles(directory)
            .Concat(
                Directory.EnumerateDirectories(directory)
                    .SelectMany(subDirectory => GetFilesInFolder(subDirectory))
        );
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
        var blob = new Blob("zip", new BlobArgs
        {
            StorageAccountName = backendStorageAccount.Name,
            StorageContainerName = container.Name,
            Type = "Block",
            Source = new FileArchive("../../TodoFunctions/bin/Release/netcoreapp2.1/publish"),
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
                    return new FirewallRule($"{ip}.", new FirewallRuleArgs
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
