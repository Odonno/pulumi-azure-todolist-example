using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Azure.Batch;
using Microsoft.WindowsAzure.Storage.Blob;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

public static class CloudBlobClientExtensions
{
    public static async Task<List<ResourceFile>> UploadFolderToContainerAsync(CloudBlobClient blobClient, string containerName, string folderPath)
    {
        List<ResourceFile> resourceFiles = new List<ResourceFile>();

        var filePaths = GetFilesInFolder(folderPath);

        foreach (string filePath in filePaths)
        {
            var resourceFile = await UploadResourceFileToContainerAsync(blobClient, containerName, folderPath, filePath);
            resourceFiles.Add(resourceFile);
        }

        return resourceFiles;
    }

    private static IEnumerable<string> GetFilesInFolder(string directory)
    {
        return Directory.EnumerateFiles(directory)
            .Concat(
                Directory.EnumerateDirectories(directory)
                    .SelectMany(subDirectory => GetFilesInFolder(subDirectory))
        );
    }

    private static async Task<ResourceFile> UploadResourceFileToContainerAsync(CloudBlobClient blobClient, string containerName, string folderPath, string filePath)
    {
        Console.WriteLine("Uploading file {0} to container [{1}]...", filePath, containerName);

        string blobName = Path.GetFullPath(filePath).Substring(folderPath.Length + 1);

        if (!new FileExtensionContentTypeProvider().TryGetContentType(filePath, out string contentType))
        {
            contentType = "application/octet-stream";
        }

        var container = blobClient.GetContainerReference(containerName);
        var blobData = container.GetBlockBlobReference(blobName);
        blobData.Properties.ContentType = contentType;

        await blobData.UploadFromFileAsync(filePath);

        // Set the expiry time and permissions for the blob shared access signature. 
        // In this case, no start time is specified, so the shared access signature becomes valid immediately
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
}
