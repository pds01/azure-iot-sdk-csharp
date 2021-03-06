﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using FluentAssertions;
using Microsoft.Azure.Devices.Common.Exceptions;
using Microsoft.Azure.Devices.E2ETests.Helpers;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Microsoft.Azure.Devices.E2ETests
{
    [TestClass]
    [TestCategory("E2E")]
    [TestCategory("IoTHub")]
    public class RegistryManagerImportDevicesTests
    {
#pragma warning disable CA1823
        private readonly TestLogging _log = TestLogging.GetInstance();

        // A bug in either Storage or System.Diagnostics causes an exception during container creation
        // so for now, we need to disable this.
        // https://github.com/Azure/azure-sdk-for-net/issues/10476
        //private readonly ConsoleEventListener _listener = TestConfig.StartEventListener();
#pragma warning restore CA1823

        private const string ImportFileNameDefault = "devices.txt";
        private const int MaxIterationWait = 30;
        private static readonly TimeSpan s_waitDuration = TimeSpan.FromSeconds(1);

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
        public async Task RegistryManager_ImportDevices()
        {
            StorageContainer storageContainer = null;
            string deviceId = $"{nameof(RegistryManager_ImportDevices)}-{StorageContainer.GetRandomSuffix(4)}";
            var registryManager = RegistryManager.CreateFromConnectionString(Configuration.IoTHub.ConnectionString);

            _log.WriteLine($"Using deviceId {deviceId}");

            try
            {
                // arrange

                string containerName = StorageContainer.BuildContainerName(nameof(RegistryManager_ImportDevices));
                storageContainer = await StorageContainer
                    .GetInstanceAsync(containerName)
                    .ConfigureAwait(false);
                _log.WriteLine($"Using container {storageContainer.Uri}");

                Stream devicesFile = ImportExportDevicesHelpers.BuildDevicesStream(
                    new List<ExportImportDevice>
                    {
                        new ExportImportDevice(
                            new Device(deviceId)
                            {
                                Authentication = new AuthenticationMechanism { Type = AuthenticationType.Sas }
                            },
                            ImportMode.Create),
                    });

                BlobClient blobClient = storageContainer.BlobContainerClient.GetBlobClient(ImportFileNameDefault);
                Response<BlobContentInfo> uploadBlobResponse = await blobClient.UploadAsync(devicesFile).ConfigureAwait(false);

                // wait for copy completion
                bool foundBlob = false;
                for (int i = 0; i < MaxIterationWait; ++i)
                {
                    await Task.Delay(s_waitDuration).ConfigureAwait(false);
                    if (await blobClient.ExistsAsync().ConfigureAwait(false))
                    {
                        foundBlob = true;
                        break;
                    }
                }
                foundBlob.Should().BeTrue($"Failed to find {ImportFileNameDefault} in storage container, required for test.");

                // act

                JobProperties importJobResponse = null;
                for (int i = 0; i < MaxIterationWait; ++i)
                {
                    try
                    {
                        importJobResponse = await registryManager
                            .ImportDevicesAsync(storageContainer.SasUri.ToString(), storageContainer.SasUri.ToString())
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
                while (s_incompleteJobs.Contains(importJobResponse.Status))
                {
                    await Task.Delay(1000).ConfigureAwait(false);
                    importJobResponse = await registryManager.GetJobAsync(importJobResponse.JobId).ConfigureAwait(false);
                }

                // assert

                importJobResponse.Status.Should().Be(JobStatus.Completed, "Otherwise import failed");
                importJobResponse.FailureReason.Should().BeNullOrEmpty("Otherwise import failed");

                // should not throw due to 404, but device may not appear immediately in registry
                Device device = null;
                for (int i = 0; i < MaxIterationWait; ++i)
                {
                    await Task.Delay(s_waitDuration).ConfigureAwait(false);
                    try
                    {
                        device = await registryManager.GetDeviceAsync(deviceId).ConfigureAwait(false);
                        break;
                    }
                    catch (Exception ex)
                    {
                        _log.WriteLine($"Could not find device on iteration {i} due to [{ex.Message}]");
                    }
                }
                if (device == null)
                {
                    Assert.Fail($"Device {deviceId} not found in registry manager");
                }
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
    }
}
