using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Shared.Protocol;
using Pulumi;
using Pulumi.Azure.AppService;
using Pulumi.Azure.AppService.Inputs;
using Pulumi.Azure.Core;
using Pulumi.Azure.Sql;
using Pulumi.Azure.Storage;

class Program
{
    private const string _sqlServerUsername = "TodoAdmin";
    private const string _sqlServerPassword = "T0d0Adm1n";

    static Task<int> Main()
    {
        return Deployment.RunAsync(() =>
        {
            // Create an Azure Resource Group
            var resourceGroup = new ResourceGroup("resourceGroup");

            // Create an Azure Static Websites (inside Storage Account)
            var frontEndpoint = CreateAzureStaticWebsites(resourceGroup);

            // Create and Azure SQL Server
            var sqlConnectionStringOutput = CreateSqlInstance(resourceGroup, out SqlServer sqlServer, out Database sqlDatabase);

            // Create an Azure Functions Account (using Service Plan and backed by Storage Account)
            var functionApp = CreateAzureFunctionApp(resourceGroup, sqlConnectionStringOutput);
            SetFirewallRulesToAccessSqlServer(resourceGroup, functionApp, sqlServer); // Give access from Functions -> SQL database

            // Export information of Azure resources created
            return new Dictionary<string, object?>
            {
                { "sqlServerConnectionString", sqlConnectionStringOutput },
                { "apiEndpoint", Output.Format($"https://{functionApp.DefaultHostname}/api") },
                { "frontEndpoint", frontEndpoint }
            };
        });
    }

    static Output<string> CreateAzureStaticWebsites(ResourceGroup resourceGroup)
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
            return GetStaticWebsiteEndpoint(frontendStorageAccountName);
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

    static Output<string> CreateSqlInstance(ResourceGroup resourceGroup, out SqlServer sqlServer, out Database sqlDatabase)
    {
        sqlServer = new SqlServer("sqlserver", new SqlServerArgs
        {
            ResourceGroupName = resourceGroup.Name,
            AdministratorLogin = _sqlServerUsername,
            AdministratorLoginPassword = _sqlServerPassword,
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

        return Output.Format($"Server=tcp:{sqlServer.FullyQualifiedDomainName};initial catalog={sqlDatabase.Name};user ID={_sqlServerUsername};password={_sqlServerPassword};Min Pool Size=0;Max Pool Size=30;Persist Security Info=true;");
    }

    static FunctionApp CreateAzureFunctionApp(ResourceGroup resourceGroup, Output<string> sqlConnectionStringOutput)
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
            Content = new FileArchive("../../TodoFunctions/bin/Debug/netcoreapp2.1/publish"),
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
                { "ConnectionString", sqlConnectionStringOutput }
            },
            SiteConfig = new FunctionAppSiteConfigArgs
            {
                Cors = new FunctionAppSiteConfigCorsArgs
                {
                    AllowedOrigins = new List<string> { "*" }
                }
            },
            StorageConnectionString = backendStorageAccount.PrimaryConnectionString,
            Version = "~2",
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
                    return new FirewallRule($"fw-{ip}", new FirewallRuleArgs
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
