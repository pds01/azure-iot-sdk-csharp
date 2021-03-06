﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using FluentAssertions;
using Microsoft.Azure.Devices.Common.Exceptions;
using Microsoft.Azure.Devices.E2ETests.Helpers;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;

namespace Microsoft.Azure.Devices.E2ETests
{
    [TestClass]
    [TestCategory("E2E")]
    [TestCategory("IoTHub")]
    public class RegistryManagerExportDevicesTests
    {
#pragma warning disable CA1823
        private readonly TestLogging _log = TestLogging.GetInstance();

        // A bug in either Storage or System.Diagnostics causes an exception during container creation
        // so for now, we need to disable this.
        // https://github.com/Azure/azure-sdk-for-net/issues/10476
        //private readonly ConsoleEventListener _listener = TestConfig.StartEventListener();
#pragma warning restore CA1823

        private const string ExportFileNameDefault = "devices.txt";
        private const int MaxIterationWait = 30;
        private static readonly TimeSpan s_waitDuration = TimeSpan.FromSeconds(1);

        private static readonly char[] s_newlines = new char[]
        {
            '\r',
            '\n',
        };

        private static readonly IReadOnlyList<JobStatus> s_incompleteJobs = new[]
        {
            JobStatus.Running,
            JobStatus.Enqueued,
            JobStatus.Queued,
            JobStatus.Scheduled,
            JobStatus.Unknown,
        };

        [TestMethod]
        [TestCategory("LongRunning")]
        [Timeout(120000)]
        public async Task RegistryManager_ExportDevices()
        {
            StorageContainer storageContainer = null;
            string deviceId = $"{nameof(RegistryManager_ExportDevices)}-{StorageContainer.GetRandomSuffix(4)}";
            var registryManager = RegistryManager.CreateFromConnectionString(Configuration.IoTHub.ConnectionString);

            _log.WriteLine($"Using deviceId {deviceId}");

            try
            {
                // arrange

                string containerName = StorageContainer.BuildContainerName(nameof(RegistryManager_ExportDevices));
                storageContainer = await StorageContainer
                    .GetInstanceAsync(containerName)
                    .ConfigureAwait(false);
                _log.WriteLine($"Using container {storageContainer.Uri}");

                await registryManager
                    .AddDeviceAsync(
                        new Device(deviceId)
                        {
                            Authentication = new AuthenticationMechanism { Type = AuthenticationType.Sas },
                        })
                    .ConfigureAwait(false);

                // act

                JobProperties exportJobResponse = null;
                for (int i = 0; i < MaxIterationWait; ++i)
                {
                    try
                    {
                        exportJobResponse = await registryManager
                           .ExportDevicesAsync(storageContainer.SasUri.ToString(), true)
                           .ConfigureAwait(false);
                        break;
                    }
                    // Concurrent jobs can be rejected, so implement a retry mechanism to handle conflicts with other tests
                    catch (JobQuotaExceededException)
                    {
                        _log.WriteLine($"JobQuoteExceededException... waiting.");
                        await Task.Delay(s_waitDuration).ConfigureAwait(false);
                        continue;
                    }
                }

                // wait for job to complete
                for (int i = 0; i < MaxIterationWait; ++i)
                {
                    await Task.Delay(s_waitDuration).ConfigureAwait(false);
                    exportJobResponse = await registryManager.GetJobAsync(exportJobResponse.JobId).ConfigureAwait(false);
                    if (!s_incompleteJobs.Contains(exportJobResponse.Status))
                    {
                        break;
                    }
                }

                // assert

                exportJobResponse.Status.Should().Be(JobStatus.Completed, "Otherwise import failed");
                exportJobResponse.FailureReason.Should().BeNullOrEmpty("Otherwise import failed");

                string content = await DownloadFileAsync(storageContainer).ConfigureAwait(false);
                string[] serializedDevices = content.Split(s_newlines, StringSplitOptions.RemoveEmptyEntries);

                bool foundDeviceInExport = false;
                foreach (string serializedDeivce in serializedDevices)
                {
                    if (serializedDeivce[0] != '{')
                    {
                        continue;
                    }

                    ExportImportDevice device = JsonConvert.DeserializeObject<ExportImportDevice>(serializedDeivce);
                    if (StringComparer.Ordinal.Equals(device.Id, deviceId))
                    {
                        _log.WriteLine($"Found device in export as [{serializedDeivce}]");
                        foundDeviceInExport = true;
                        break;
                    }
                }
                foundDeviceInExport.Should().BeTrue();
            }
            finally
            {
                try
                {
                    storageContainer?.Dispose();

                    await registryManager.RemoveDeviceAsync(deviceId).ConfigureAwait(false);
                }
                catch { }
            }
        }

        private static async Task<string> DownloadFileAsync(StorageContainer storageContainer)
        {
            BlobClient exportFile = storageContainer.BlobContainerClient.GetBlobClient(ExportFileNameDefault);
            Response<BlobDownloadInfo> download = await exportFile.DownloadAsync().ConfigureAwait(false);
            string content = ReadStream(download.Value.Content);
            return content;
        }

        private static string ReadStream(Stream stream)
        {
            using (var streamReader = new StreamReader(stream))
            {
                return streamReader.ReadToEnd();
            }
        }
    }
}
