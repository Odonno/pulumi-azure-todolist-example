using System.Collections.Generic;
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
    static Task<int> Main()
    {
        return Deployment.RunAsync(() =>
        {
            // Create an Azure Resource Group
            var resourceGroup = new ResourceGroup("resourceGroup");

            // Create an Azure Storage Account
            var storageAccount = new Account("storage", new AccountArgs
            {
                ResourceGroupName = resourceGroup.Name,
                AccountReplicationType = "LRS",
                EnableHttpsTrafficOnly = true,
                AccountTier = "Standard",
                AccountKind = "StorageV2",
                AccessTier = "Hot",
            });

            storageAccount.PrimaryBlobConnectionString.Apply(async v => await EnableStaticSites(v));

            // TODO : Deploy React Website

            // Create App Service Plan
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

            // Create and Azure SQL Server
            string sqlServerUsername = "TodoAdmin";
            string sqlServerPassword = "T0d0Adm1n";

            var sqlServer = new SqlServer("sqlserver", new SqlServerArgs
            {
                ResourceGroupName = resourceGroup.Name,
                AdministratorLogin = sqlServerUsername,
                AdministratorLoginPassword = sqlServerPassword,
                Version = "12.0"
            });
            
            var sqlDatabase = new Database("sqldatabase", new DatabaseArgs
            {
                ResourceGroupName = resourceGroup.Name,
                ServerName = sqlServer.Name,
                MaxSizeBytes = 104857600.ToString(),
                RequestedServiceObjectiveName = "Basic",
                Edition = "Basic"
            });

            // Create an Azure Functions Account
            var container = new Container("zips", new ContainerArgs
            {
                StorageAccountName = storageAccount.Name,
                ContainerAccessType = "private",
            });
            var blob = new ZipBlob("zip", new ZipBlobArgs
            {
                StorageAccountName = storageAccount.Name,
                StorageContainerName = container.Name,
                Type = "block",
                Content = new FileArchive("../../TodoFunctions/bin/Debug/netcoreapp2.1/publish"),
            });
            var codeBlobUrl = SharedAccessSignature.SignedBlobReadUrl(blob, storageAccount);

            var sqlConnectionStringOutput = Output.Format($"Server=tcp:{sqlServer.FullyQualifiedDomainName};initial catalog={sqlDatabase.Name};user ID={sqlServerUsername};password={sqlServerPassword};Min Pool Size=0;Max Pool Size=30;Persist Security Info=true;");

            var app = new FunctionApp("app", new FunctionAppArgs
            {
                ResourceGroupName = resourceGroup.Name,
                AppServicePlanId = appServicePlan.Id,
                AppSettings =
                {
                    { "runtime", "dotnet" },
                    { "WEBSITE_RUN_FROM_PACKAGE", codeBlobUrl },
                    { "ConnectionString", sqlConnectionStringOutput }
                },
                StorageConnectionString = storageAccount.PrimaryConnectionString,
                Version = "~2",
            });

            // Export the connection string for the storage account
            return new Dictionary<string, object?>
            {
                { "blobStorageConnectionString", storageAccount.PrimaryConnectionString },
                { "sqlServerConnectionString", sqlConnectionStringOutput },
                { "apiEndpoint", Output.Format($"https://{app.DefaultHostname}/api") },
            };
        });
    }

    static async Task EnableStaticSites(string connectionString)
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

        await blobClient.SetServicePropertiesAsync(blobServiceProperties);
    }
}
