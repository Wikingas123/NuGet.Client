﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using Moq;
using NuGet.Frameworks;
using NuGet.PackageManagement;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.Protocol.Core.Types;
using NuGet.Resolver;
using NuGet.Test.Utility;
using NuGet.Versioning;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Test.Utility;
using Xunit;
using Strings = NuGet.ProjectManagement.Strings;

namespace NuGet.Test
{
    public class NuGetPackageManagerTests
    {
        // Following are the various sets of packages that are small in size. To be used by the functional tests
        private readonly List<PackageIdentity> NoDependencyLibPackages = new List<PackageIdentity>
            {
                new PackageIdentity("Microsoft.AspNet.Razor", new NuGetVersion("2.0.30506")),
                new PackageIdentity("Microsoft.AspNet.Razor", new NuGetVersion("3.0.0")),
                new PackageIdentity("Microsoft.AspNet.Razor", new NuGetVersion("3.2.0-rc")),
                new PackageIdentity("Antlr", new NuGetVersion("3.5.0.2"))
            };

        private readonly List<PackageIdentity> PackageWithDependents = new List<PackageIdentity>
            {
                new PackageIdentity("jQuery", new NuGetVersion("1.4.4")),
                new PackageIdentity("jQuery", new NuGetVersion("1.6.4")),
                new PackageIdentity("jQuery.Validation", new NuGetVersion("1.13.1")),
                new PackageIdentity("jQuery.UI.Combined", new NuGetVersion("1.11.2"))
            };

        private readonly List<PackageIdentity> PackageWithDeepDependency = new List<PackageIdentity>
            {
                new PackageIdentity("Microsoft.Data.Edm", new NuGetVersion("5.6.2")),
                new PackageIdentity("Microsoft.WindowsAzure.ConfigurationManager", new NuGetVersion("1.8.0.0")),
                new PackageIdentity("Newtonsoft.Json", new NuGetVersion("5.0.8")),
                new PackageIdentity("System.Spatial", new NuGetVersion("5.6.2")),
                new PackageIdentity("Microsoft.Data.OData", new NuGetVersion("5.6.2")),
                new PackageIdentity("Microsoft.Data.Services.Client", new NuGetVersion("5.6.2")),
                new PackageIdentity("WindowsAzure.Storage", new NuGetVersion("4.3.0"))
            };

        private readonly List<PackageIdentity> MorePackageWithDependents = new List<PackageIdentity>
            {
                new PackageIdentity("Microsoft.Bcl.Build", new NuGetVersion("1.0.14")),
                new PackageIdentity("Microsoft.Bcl.Build", new NuGetVersion("1.0.21")),
                new PackageIdentity("Microsoft.Bcl", new NuGetVersion("1.1.9")),
                new PackageIdentity("Microsoft.Net.Http", new NuGetVersion("2.2.22")),
                new PackageIdentity("Microsoft.Net.Http", new NuGetVersion("2.2.28"))
            };

        private readonly List<PackageIdentity> LatestAspNetPackages = new List<PackageIdentity>
            {
                new PackageIdentity("Microsoft.AspNet.Mvc", new NuGetVersion("6.0.0-beta3")),
                new PackageIdentity("Microsoft.AspNet.Mvc.Razor", new NuGetVersion("6.0.0-beta3")),
                new PackageIdentity("Microsoft.AspNet.Mvc.Core", new NuGetVersion("6.0.0-beta3"))
            };

        // Install and uninstall a package while calling get installed on another thread
        [Fact]
        public async Task TestPacManInstallAndRequestInstalledPackages()
        {
            using (var packageSource = TestFileSystemUtility.CreateRandomTestFolder())
            {
                // Arrange
                var sourceRepositoryProvider = TestSourceRepositoryUtility.CreateSourceRepositoryProvider(
                    new List<Configuration.PackageSource>()
                    {
                        new Configuration.PackageSource(packageSource.Path)
                    });

                using (var testSolutionManager = new TestSolutionManager(true))
                {
                    var testSettings = new Configuration.NullSettings();
                    var token = CancellationToken.None;
                    var resolutionContext = new ResolutionContext();
                    var testNuGetProjectContext = new TestNuGetProjectContext();
                    var deleteOnRestartManager = new TestDeleteOnRestartManager();
                    var nuGetPackageManager = new NuGetPackageManager(
                        sourceRepositoryProvider,
                        testSettings,
                        testSolutionManager,
                        deleteOnRestartManager);
                    var packagesFolderPath = PackagesFolderPathUtility.GetPackagesFolderPath(testSolutionManager, testSettings);
                    var packagePathResolver = new PackagePathResolver(packagesFolderPath);
                    var projectA = testSolutionManager.AddNewMSBuildProject();

                    var builder = new Packaging.PackageBuilder()
                    {
                        Id = "packageA",
                        Version = NuGetVersion.Parse("1.0.0"),
                        Description = "Descriptions",
                    };

                    builder.Authors.Add("testAuthor");
                    builder.Files.Add(CreatePackageFile(@"lib" + Path.DirectorySeparatorChar + "net45" + Path.DirectorySeparatorChar + "_._"));

                    using (var stream = File.OpenWrite(Path.Combine(packageSource, "packagea.1.0.0.nupkg")))
                    {
                        builder.Save(stream);
                    }

                    var run = true;

                    var getInstalledTask = Task.Run(async () =>
                    {
                        // Get the list of installed packages
                        while (run)
                        {
                            var projectAInstalled = (await projectA.GetInstalledPackagesAsync(token)).ToList();
                        }
                    });

                    // Act
                    // Install and Uninstall 50 times while polling for installed packages
                    for (int i = 0; i < 50; i++)
                    {
                        // Install
                        await nuGetPackageManager.InstallPackageAsync(projectA, "packageA",
                            resolutionContext, testNuGetProjectContext, sourceRepositoryProvider.GetRepositories().First(), null, token);

                        // Uninstall
                        await nuGetPackageManager.UninstallPackageAsync(
                            projectA,
                            "packageA",
                            new UninstallationContext(removeDependencies: false, forceRemove: true),
                            testNuGetProjectContext,
                            token);
                    }

                    // Check for exceptions thrown by the get installed task
                    run = false;
                    await getInstalledTask;

                    var installed = (await projectA.GetInstalledPackagesAsync(token)).ToList();

                    // Assert
                    // Verify no exceptions and that the final package was removed
                    Assert.Equal(0, installed.Count);
                }
            }
        }

        [Fact]
        public async Task TestPacManInstallPackage()
        {
            // Arrange
            var sourceRepositoryProvider = TestSourceRepositoryUtility.CreateV3OnlySourceRepositoryProvider();
            using (var testSolutionManager = new TestSolutionManager(true))
            {
                var testSettings = new Configuration.NullSettings();
                var token = CancellationToken.None;
                var deleteOnRestartManager = new TestDeleteOnRestartManager();
                var nuGetPackageManager = new NuGetPackageManager(
                    sourceRepositoryProvider,
                    testSettings,
                    testSolutionManager,
                    deleteOnRestartManager);
                var packagesFolderPath = PackagesFolderPathUtility.GetPackagesFolderPath(testSolutionManager, testSettings);

                var msBuildNuGetProject = testSolutionManager.AddNewMSBuildProject();
                var msBuildNuGetProjectSystem = msBuildNuGetProject.MSBuildNuGetProjectSystem as TestMSBuildNuGetProjectSystem;
                var packagesConfigPath = msBuildNuGetProject.PackagesConfigNuGetProject.FullPath;
                var packageIdentity = NoDependencyLibPackages[0];

                // Pre-Assert
                // Check that the packages.config file does not exist
                Assert.False(File.Exists(packagesConfigPath));
                // Check that there are no packages returned by PackagesConfigProject
                var packagesInPackagesConfig = (await msBuildNuGetProject.PackagesConfigNuGetProject.GetInstalledPackagesAsync(token)).ToList();
                Assert.Equal(0, packagesInPackagesConfig.Count);
                Assert.Equal(0, msBuildNuGetProjectSystem.References.Count);

                // Act
                await nuGetPackageManager.InstallPackageAsync(msBuildNuGetProject, packageIdentity,
                    new ResolutionContext(), new TestNuGetProjectContext(), sourceRepositoryProvider.GetRepositories().First(), null, token);

                // Assert
                // Check that the packages.config file exists after the installation
                Assert.True(File.Exists(packagesConfigPath));
                // Check the number of packages and packages returned by PackagesConfigProject after the installation
                packagesInPackagesConfig = (await msBuildNuGetProject.PackagesConfigNuGetProject.GetInstalledPackagesAsync(token)).ToList();
                Assert.Equal(1, packagesInPackagesConfig.Count);
                Assert.Equal(packageIdentity, packagesInPackagesConfig[0].PackageIdentity);
                Assert.Equal(msBuildNuGetProject.MSBuildNuGetProjectSystem.TargetFramework, packagesInPackagesConfig[0].TargetFramework);
            }
        }

        [Fact]
        public async Task PackagesConfigNuGetProjectGetInstalledPackagesListInvalidXml()
        {
            // Arrange
            var sourceRepositoryProvider = TestSourceRepositoryUtility.CreateV3OnlySourceRepositoryProvider();
            using (var testSolutionManager = new TestSolutionManager(true))
            {
                var testSettings = new Configuration.NullSettings();
                var token = CancellationToken.None;
                var deleteOnRestartManager = new TestDeleteOnRestartManager();
                var nuGetPackageManager = new NuGetPackageManager(
                    sourceRepositoryProvider,
                    testSettings,
                    testSolutionManager,
                    deleteOnRestartManager);
                var packagesFolderPath = PackagesFolderPathUtility.GetPackagesFolderPath(testSolutionManager, testSettings);

                var msBuildNuGetProject = testSolutionManager.AddNewMSBuildProject();
                var msBuildNuGetProjectSystem = msBuildNuGetProject.MSBuildNuGetProjectSystem as TestMSBuildNuGetProjectSystem;
                var packagesConfigPath = msBuildNuGetProject.PackagesConfigNuGetProject.FullPath;
                var packageIdentity = NoDependencyLibPackages[0];

                // Create pacakges.config that is an invalid xml
                using (var w = new StreamWriter(File.Create(packagesConfigPath)))
                {
                    w.Write("abc");
                }

                // Act and Assert
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                {
                    await msBuildNuGetProject.PackagesConfigNuGetProject.GetInstalledPackagesAsync(token);
                });

                Assert.True(ex.Message.StartsWith("An error occurred while reading file"));
            }
        }

        [Fact]
        public async Task TestPacManInstallPackageAlreadyInstalledException()
        {
            // Arrange
            var sourceRepositoryProvider = TestSourceRepositoryUtility.CreateV3OnlySourceRepositoryProvider();
            using (var testSolutionManager = new TestSolutionManager(true))
            {
                var testSettings = new Configuration.NullSettings();
                var token = CancellationToken.None;
                var deleteOnRestartManager = new TestDeleteOnRestartManager();
                var nuGetPackageManager = new NuGetPackageManager(
                    sourceRepositoryProvider,
                    testSettings,
                    testSolutionManager,
                    deleteOnRestartManager);
                var packagesFolderPath = PackagesFolderPathUtility.GetPackagesFolderPath(testSolutionManager, testSettings);

                var msBuildNuGetProject = testSolutionManager.AddNewMSBuildProject();
                var msBuildNuGetProjectSystem = msBuildNuGetProject.MSBuildNuGetProjectSystem as TestMSBuildNuGetProjectSystem;
                var packagesConfigPath = msBuildNuGetProject.PackagesConfigNuGetProject.FullPath;
                var packageIdentity = NoDependencyLibPackages[0];

                // Pre-Assert
                // Check that the packages.config file does not exist
                Assert.False(File.Exists(packagesConfigPath));
                // Check that there are no packages returned by PackagesConfigProject
                var packagesInPackagesConfig = (await msBuildNuGetProject.PackagesConfigNuGetProject.GetInstalledPackagesAsync(token)).ToList();
                Assert.Equal(0, packagesInPackagesConfig.Count);
                Assert.Equal(0, msBuildNuGetProjectSystem.References.Count);

                // Act
                await nuGetPackageManager.InstallPackageAsync(msBuildNuGetProject, packageIdentity,
                    new ResolutionContext(), new TestNuGetProjectContext(), sourceRepositoryProvider.GetRepositories().First(), null, token);

                // Assert
                // Check that the packages.config file exists after the installation
                Assert.True(File.Exists(packagesConfigPath));
                // Check the number of packages and packages returned by PackagesConfigProject after the installation
                packagesInPackagesConfig = (await msBuildNuGetProject.PackagesConfigNuGetProject.GetInstalledPackagesAsync(token)).ToList();
                Assert.Equal(1, packagesInPackagesConfig.Count);
                Assert.Equal(packageIdentity, packagesInPackagesConfig[0].PackageIdentity);
                Assert.Equal(msBuildNuGetProject.MSBuildNuGetProjectSystem.TargetFramework, packagesInPackagesConfig[0].TargetFramework);

                InvalidOperationException alreadyInstalledException = null;
                try
                {
                    await nuGetPackageManager.InstallPackageAsync(msBuildNuGetProject, packageIdentity,
                        new ResolutionContext(), new TestNuGetProjectContext(), sourceRepositoryProvider.GetRepositories().First(), null, token);
                }
                catch (InvalidOperationException ex)
                {
                    alreadyInstalledException = ex;
                }

                Assert.NotNull(alreadyInstalledException);
                Assert.Equal(string.Format(Strings.PackageAlreadyExistsInProject, packageIdentity, msBuildNuGetProjectSystem.ProjectName),
                    alreadyInstalledException.Message);
                Assert.Equal(alreadyInstalledException.InnerException.GetType(), typeof(PackageAlreadyInstalledException));
            }
        }

        [Fact]
        public async Task TestPacManInstallDifferentPackageAfterInstall()
        {
            // Arrange
            var sourceRepositoryProvider = TestSourceRepositoryUtility.CreateV3OnlySourceRepositoryProvider();
            using (var testSolutionManager = new TestSolutionManager(true))
            {
                var testSettings = new Configuration.NullSettings();
                var token = CancellationToken.None;
                var deleteOnRestartManager = new TestDeleteOnRestartManager();
                var nuGetPackageManager = new NuGetPackageManager(
                    sourceRepositoryProvider,
                    testSettings,
                    testSolutionManager,
                    deleteOnRestartManager);
                var packagesFolderPath = PackagesFolderPathUtility.GetPackagesFolderPath(testSolutionManager, testSettings);

                var msBuildNuGetProject = testSolutionManager.AddNewMSBuildProject();
                var msBuildNuGetProjectSystem = msBuildNuGetProject.MSBuildNuGetProjectSystem as TestMSBuildNuGetProjectSystem;
                var packagesConfigPath = msBuildNuGetProject.PackagesConfigNuGetProject.FullPath;
                var firstPackageIdentity = NoDependencyLibPackages[0];

                // Pre-Assert
                // Check that the packages.config file does not exist
                Assert.False(File.Exists(packagesConfigPath));
                // Check that there are no packages returned by PackagesConfigProject
                var packagesInPackagesConfig = (await msBuildNuGetProject.PackagesConfigNuGetProject.GetInstalledPackagesAsync(token)).ToList();
                Assert.Equal(0, packagesInPackagesConfig.Count);
                Assert.Equal(0, msBuildNuGetProjectSystem.References.Count);

                // Act
                await nuGetPackageManager.InstallPackageAsync(msBuildNuGetProject, firstPackageIdentity,
                    new ResolutionContext(), new TestNuGetProjectContext(), sourceRepositoryProvider.GetRepositories().First(), null, token);

                var secondPackageIdentity = NoDependencyLibPackages[3];
                await nuGetPackageManager.InstallPackageAsync(msBuildNuGetProject, secondPackageIdentity,
                    new ResolutionContext(), new TestNuGetProjectContext(), sourceRepositoryProvider.GetRepositories().First(), null, token);

                // Assert
                // Check that the packages.config file exists after the installation
                Assert.True(File.Exists(packagesConfigPath));
                // Check the number of packages and packages returned by PackagesConfigProject after the installation
                packagesInPackagesConfig = (await msBuildNuGetProject.PackagesConfigNuGetProject.GetInstalledPackagesAsync(token)).ToList();
                Assert.Equal(2, packagesInPackagesConfig.Count);
                Assert.Equal(firstPackageIdentity, packagesInPackagesConfig[1].PackageIdentity);
                Assert.Equal(msBuildNuGetProject.MSBuildNuGetProjectSystem.TargetFramework, packagesInPackagesConfig[1].TargetFramework);
                Assert.Equal(secondPackageIdentity, packagesInPackagesConfig[0].PackageIdentity);
                Assert.Equal(msBuildNuGetProject.MSBuildNuGetProjectSystem.TargetFramework, packagesInPackagesConfig[0].TargetFramework);
            }
        }

        [Fact]
        public async Task TestPacManInstallSamePackageAfterInstall()
        {
            // Arrange
            var sourceRepositoryProvider = TestSourceRepositoryUtility.CreateV3OnlySourceRepositoryProvider();
            using (var testSolutionManager = new TestSolutionManager(true))
            {
                var testSettings = new Configuration.NullSettings();
                var token = CancellationToken.None;
                var deleteOnRestartManager = new TestDeleteOnRestartManager();
                var nuGetPackageManager = new NuGetPackageManager(
                    sourceRepositoryProvider,
                    testSettings,
                    testSolutionManager,
                    deleteOnRestartManager);
                var packagesFolderPath = PackagesFolderPathUtility.GetPackagesFolderPath(testSolutionManager, testSettings);

                var msBuildNuGetProject = testSolutionManager.AddNewMSBuildProject();
                var msBuildNuGetProjectSystem = msBuildNuGetProject.MSBuildNuGetProjectSystem as TestMSBuildNuGetProjectSystem;
                var packagesConfigPath = msBuildNuGetProject.PackagesConfigNuGetProject.FullPath;
                var firstPackageIdentity = NoDependencyLibPackages[0];

                // Pre-Assert
                // Check that the packages.config file does not exist
                Assert.False(File.Exists(packagesConfigPath));
                // Check that there are no packages returned by PackagesConfigProject
                var packagesInPackagesConfig = (await msBuildNuGetProject.PackagesConfigNuGetProject.GetInstalledPackagesAsync(token)).ToList();
                Assert.Equal(0, packagesInPackagesConfig.Count);
                Assert.Equal(0, msBuildNuGetProjectSystem.References.Count);

                // Act
                await nuGetPackageManager.InstallPackageAsync(msBuildNuGetProject, firstPackageIdentity,
                    new ResolutionContext(), new TestNuGetProjectContext(), sourceRepositoryProvider.GetRepositories().First(), null, token);

                var secondPackageIdentity = NoDependencyLibPackages[1];
                await nuGetPackageManager.InstallPackageAsync(msBuildNuGetProject, secondPackageIdentity,
                    new ResolutionContext(), new TestNuGetProjectContext(), sourceRepositoryProvider.GetRepositories().First(), null, token);

                // Assert
                // Check that the packages.config file exists after the installation
                Assert.True(File.Exists(packagesConfigPath));
                // Check the number of packages and packages returned by PackagesConfigProject after the installation
                packagesInPackagesConfig = (await msBuildNuGetProject.PackagesConfigNuGetProject.GetInstalledPackagesAsync(token)).ToList();
                Assert.Equal(1, packagesInPackagesConfig.Count);
                Assert.Equal(secondPackageIdentity, packagesInPackagesConfig[0].PackageIdentity);
                Assert.Equal(msBuildNuGetProject.MSBuildNuGetProjectSystem.TargetFramework, packagesInPackagesConfig[0].TargetFramework);
            }
        }

        [Fact]
        public async Task TestPacManInstallPackageWithDependents()
        {
            // Arrange
            var sourceRepositoryProvider = TestSourceRepositoryUtility.CreateV3OnlySourceRepositoryProvider();
            using (var testSolutionManager = new TestSolutionManager(true))
            {
                var testSettings = new Configuration.NullSettings();
                var token = CancellationToken.None;
                var deleteOnRestartManager = new TestDeleteOnRestartManager();
                var nuGetPackageManager = new NuGetPackageManager(
                    sourceRepositoryProvider,
                    testSettings,
                    testSolutionManager,
                    deleteOnRestartManager);
                var packagesFolderPath = PackagesFolderPathUtility.GetPackagesFolderPath(testSolutionManager, testSettings);

                var msBuildNuGetProject = testSolutionManager.AddNewMSBuildProject();
                var msBuildNuGetProjectSystem = msBuildNuGetProject.MSBuildNuGetProjectSystem as TestMSBuildNuGetProjectSystem;
                var packagesConfigPath = msBuildNuGetProject.PackagesConfigNuGetProject.FullPath;
                var packageIdentity = PackageWithDependents[2];

                // Pre-Assert
                // Check that the packages.config file does not exist
                Assert.False(File.Exists(packagesConfigPath));
                // Check that there are no packages returned by PackagesConfigProject
                var packagesInPackagesConfig = (await msBuildNuGetProject.PackagesConfigNuGetProject.GetInstalledPackagesAsync(token)).ToList();
                Assert.Equal(0, packagesInPackagesConfig.Count);
                Assert.Equal(0, msBuildNuGetProjectSystem.References.Count);

                // Act
                await nuGetPackageManager.InstallPackageAsync(msBuildNuGetProject, packageIdentity,
                    new ResolutionContext(), new TestNuGetProjectContext(), sourceRepositoryProvider.GetRepositories().First(), null, token);

                // Assert
                // Check that the packages.config file exists after the installation
                Assert.True(File.Exists(packagesConfigPath));
                // Check the number of packages and packages returned by PackagesConfigProject after the installation
                packagesInPackagesConfig = (await msBuildNuGetProject.PackagesConfigNuGetProject.GetInstalledPackagesAsync(token)).ToList();
                Assert.Equal(2, packagesInPackagesConfig.Count);
                Assert.Equal(packageIdentity, packagesInPackagesConfig[1].PackageIdentity);
                Assert.Equal(msBuildNuGetProject.MSBuildNuGetProjectSystem.TargetFramework, packagesInPackagesConfig[1].TargetFramework);
                Assert.Equal(PackageWithDependents[0], packagesInPackagesConfig[0].PackageIdentity);
                Assert.Equal(msBuildNuGetProject.MSBuildNuGetProjectSystem.TargetFramework, packagesInPackagesConfig[0].TargetFramework);
            }
        }

        [Fact]
        public async Task TestPacManPreviewInstallOrderOfDependencies()
        {
            // Arrange
            var sourceRepositoryProvider = TestSourceRepositoryUtility.CreateV2OnlySourceRepositoryProvider();
            using (var testSolutionManager = new TestSolutionManager(true))
            {
                var testSettings = new Configuration.NullSettings();
                var token = CancellationToken.None;
                var deleteOnRestartManager = new TestDeleteOnRestartManager();
                var nuGetPackageManager = new NuGetPackageManager(
                    sourceRepositoryProvider,
                    testSettings,
                    testSolutionManager,
                    deleteOnRestartManager);
                var packagesFolderPath = PackagesFolderPathUtility.GetPackagesFolderPath(testSolutionManager, testSettings);

                var msBuildNuGetProject = testSolutionManager.AddNewMSBuildProject();
                var msBuildNuGetProjectSystem = msBuildNuGetProject.MSBuildNuGetProjectSystem as TestMSBuildNuGetProjectSystem;
                var packagesConfigPath = msBuildNuGetProject.PackagesConfigNuGetProject.FullPath;
                var packageIdentity = MorePackageWithDependents[3];

                // Pre-Assert
                // Check that the packages.config file does not exist
                Assert.False(File.Exists(packagesConfigPath));
                // Check that there are no packages returned by PackagesConfigProject
                var packagesInPackagesConfig = (await msBuildNuGetProject.PackagesConfigNuGetProject.GetInstalledPackagesAsync(token)).ToList();
                Assert.Equal(0, packagesInPackagesConfig.Count);
                Assert.Equal(0, msBuildNuGetProjectSystem.References.Count);

                // Act
                var packageActions = (await nuGetPackageManager.PreviewInstallPackageAsync(msBuildNuGetProject, packageIdentity,
                    new ResolutionContext(), new TestNuGetProjectContext(), sourceRepositoryProvider.GetRepositories().First(), null, token)).ToList();

                // Assert
                Assert.Equal(3, packageActions.Count);
                Assert.True(MorePackageWithDependents[0].Equals(packageActions[0].PackageIdentity));
                Assert.Equal(NuGetProjectActionType.Install, packageActions[0].NuGetProjectActionType);
                Assert.Equal(sourceRepositoryProvider.GetRepositories().Single().PackageSource.Source,
                    packageActions[0].SourceRepository.PackageSource.Source);
                Assert.True(MorePackageWithDependents[2].Equals(packageActions[1].PackageIdentity));
                Assert.Equal(NuGetProjectActionType.Install, packageActions[1].NuGetProjectActionType);
                Assert.Equal(sourceRepositoryProvider.GetRepositories().Single().PackageSource.Source,
                    packageActions[0].SourceRepository.PackageSource.Source);
                Assert.True(MorePackageWithDependents[3].Equals(packageActions[2].PackageIdentity));
                Assert.Equal(NuGetProjectActionType.Install, packageActions[2].NuGetProjectActionType);
                Assert.Equal(sourceRepositoryProvider.GetRepositories().Single().PackageSource.Source,
                    packageActions[0].SourceRepository.PackageSource.Source);
            }
        }

        [Fact]
        public async Task TestPacManUninstallPackage()
        {
            // Arrange
            var sourceRepositoryProvider = TestSourceRepositoryUtility.CreateV3OnlySourceRepositoryProvider();
            using (var testSolutionManager = new TestSolutionManager(true))
            {
                var testSettings = new Configuration.NullSettings();
                var token = CancellationToken.None;
                var resolutionContext = new ResolutionContext();
                var testNuGetProjectContext = new TestNuGetProjectContext();
                var deleteOnRestartManager = new TestDeleteOnRestartManager();
                var nuGetPackageManager = new NuGetPackageManager(
                    sourceRepositoryProvider,
                    testSettings,
                    testSolutionManager,
                    deleteOnRestartManager);
                var packagesFolderPath = PackagesFolderPathUtility.GetPackagesFolderPath(testSolutionManager, testSettings);
                var packagePathResolver = new PackagePathResolver(packagesFolderPath);

                var msBuildNuGetProject = testSolutionManager.AddNewMSBuildProject();
                var msBuildNuGetProjectSystem = msBuildNuGetProject.MSBuildNuGetProjectSystem as TestMSBuildNuGetProjectSystem;
                var packagesConfigPath = msBuildNuGetProject.PackagesConfigNuGetProject.FullPath;
                var packageIdentity = NoDependencyLibPackages[0];

                // Pre-Assert
                // Check that the packages.config file does not exist
                Assert.False(File.Exists(packagesConfigPath));
                // Check that there are no packages returned by PackagesConfigProject
                var packagesInPackagesConfig = (await msBuildNuGetProject.PackagesConfigNuGetProject.GetInstalledPackagesAsync(token)).ToList();
                Assert.Equal(0, packagesInPackagesConfig.Count);
                Assert.Equal(0, msBuildNuGetProjectSystem.References.Count);

                // Act
                await nuGetPackageManager.InstallPackageAsync(msBuildNuGetProject, packageIdentity,
                    resolutionContext, testNuGetProjectContext, sourceRepositoryProvider.GetRepositories().First(), null, token);

                // Assert
                // Check that the packages.config file exists after the installation
                Assert.True(File.Exists(packagesConfigPath));
                // Check the number of packages and packages returned by PackagesConfigProject after the installation
                packagesInPackagesConfig = (await msBuildNuGetProject.PackagesConfigNuGetProject.GetInstalledPackagesAsync(token)).ToList();
                Assert.Equal(1, packagesInPackagesConfig.Count);
                Assert.Equal(packageIdentity, packagesInPackagesConfig[0].PackageIdentity);
                Assert.Equal(msBuildNuGetProject.MSBuildNuGetProjectSystem.TargetFramework, packagesInPackagesConfig[0].TargetFramework);

                // Main Act
                var uninstallationContext = new UninstallationContext();
                await nuGetPackageManager.UninstallPackageAsync(msBuildNuGetProject, packageIdentity.Id,
                    uninstallationContext, testNuGetProjectContext, token);

                // Assert
                // Check that the packages.config file exists after the installation
                Assert.True(!File.Exists(packagesConfigPath));
                // Check the number of packages and packages returned by PackagesConfigProject after the installation
                packagesInPackagesConfig = (await msBuildNuGetProject.PackagesConfigNuGetProject.GetInstalledPackagesAsync(token)).ToList();
                Assert.Equal(0, packagesInPackagesConfig.Count);
                Assert.False(File.Exists(packagePathResolver.GetInstalledPackageFilePath(packageIdentity)));
            }
        }

        [Fact]
        public async Task TestPacManUninstallDependencyPackage()
        {
            // Arrange
            var sourceRepositoryProvider = TestSourceRepositoryUtility.CreateV3OnlySourceRepositoryProvider();
            using (var testSolutionManager = new TestSolutionManager(true))
            {
                var testSettings = new Configuration.NullSettings();
                var token = CancellationToken.None;
                var resolutionContext = new ResolutionContext();
                var testNuGetProjectContext = new TestNuGetProjectContext();
                var packagesFolderPath = PackagesFolderPathUtility.GetPackagesFolderPath(testSolutionManager, testSettings);
                var deleteOnRestartManager = new TestDeleteOnRestartManager();
                var nuGetPackageManager = new NuGetPackageManager(
                    sourceRepositoryProvider,
                    testSettings,
                    testSolutionManager,
                    deleteOnRestartManager);

                var msBuildNuGetProject = testSolutionManager.AddNewMSBuildProject();
                var msBuildNuGetProjectSystem = msBuildNuGetProject.MSBuildNuGetProjectSystem as TestMSBuildNuGetProjectSystem;
                var packagesConfigPath = msBuildNuGetProject.PackagesConfigNuGetProject.FullPath;
                var packageIdentity = PackageWithDependents[2];

                // Pre-Assert
                // Check that the packages.config file does not exist
                Assert.False(File.Exists(packagesConfigPath));
                // Check that there are no packages returned by PackagesConfigProject
                var packagesInPackagesConfig = (await msBuildNuGetProject.PackagesConfigNuGetProject.GetInstalledPackagesAsync(token)).ToList();
                Assert.Equal(0, packagesInPackagesConfig.Count);
                Assert.Equal(0, msBuildNuGetProjectSystem.References.Count);

                // Act
                await nuGetPackageManager.InstallPackageAsync(msBuildNuGetProject, packageIdentity,
                    resolutionContext, testNuGetProjectContext, sourceRepositoryProvider.GetRepositories().First(), null, token);

                // Assert
                // Check that the packages.config file exists after the installation
                Assert.True(File.Exists(packagesConfigPath));
                // Check the number of packages and packages returned by PackagesConfigProject after the installation
                packagesInPackagesConfig = (await msBuildNuGetProject.PackagesConfigNuGetProject.GetInstalledPackagesAsync(token)).ToList();
                Assert.Equal(2, packagesInPackagesConfig.Count);
                Assert.Equal(packageIdentity, packagesInPackagesConfig[1].PackageIdentity);
                Assert.Equal(msBuildNuGetProject.MSBuildNuGetProjectSystem.TargetFramework, packagesInPackagesConfig[1].TargetFramework);
                Assert.Equal(PackageWithDependents[0], packagesInPackagesConfig[0].PackageIdentity);
                Assert.Equal(msBuildNuGetProject.MSBuildNuGetProjectSystem.TargetFramework, packagesInPackagesConfig[0].TargetFramework);

                // Main Act
                Exception exception = null;
                try
                {
                    var uninstallationContext = new UninstallationContext();
                    await nuGetPackageManager.UninstallPackageAsync(msBuildNuGetProject, "jQuery",
                        uninstallationContext, testNuGetProjectContext, token);
                }
                catch (InvalidOperationException ex)
                {
                    exception = ex;
                }
                catch (AggregateException ex)
                {
                    exception = ExceptionUtility.Unwrap(ex);
                }

                Assert.NotNull(exception);
                Assert.True(exception is InvalidOperationException);
                Assert.Equal("Unable to uninstall 'jQuery.1.4.4' because 'jQuery.Validation.1.13.1' depends on it.",
                    exception.Message);
            }
        }

        [Fact]
        public async Task TestPacManPreviewUninstallDependencyPackage()
        {
            // Arrange
            var sourceRepositoryProvider = TestSourceRepositoryUtility.CreateV3OnlySourceRepositoryProvider();
            using (var testSolutionManager = new TestSolutionManager(true))
            {
                var testSettings = new Configuration.NullSettings();
                var token = CancellationToken.None;
                var resolutionContext = new ResolutionContext();
                var testNuGetProjectContext = new TestNuGetProjectContext();
                var packagesFolderPath = PackagesFolderPathUtility.GetPackagesFolderPath(testSolutionManager, testSettings);
                var deleteOnRestartManager = new TestDeleteOnRestartManager();
                var nuGetPackageManager = new NuGetPackageManager(
                    sourceRepositoryProvider,
                    testSettings,
                    testSolutionManager,
                    deleteOnRestartManager);

                var msBuildNuGetProject = testSolutionManager.AddNewMSBuildProject();
                var msBuildNuGetProjectSystem = msBuildNuGetProject.MSBuildNuGetProjectSystem as TestMSBuildNuGetProjectSystem;
                var packagesConfigPath = msBuildNuGetProject.PackagesConfigNuGetProject.FullPath;
                var packageIdentity = PackageWithDependents[2];

                // Pre-Assert
                // Check that the packages.config file does not exist
                Assert.False(File.Exists(packagesConfigPath));
                // Check that there are no packages returned by PackagesConfigProject
                var packagesInPackagesConfig = (await msBuildNuGetProject.PackagesConfigNuGetProject.GetInstalledPackagesAsync(token)).ToList();
                Assert.Equal(0, packagesInPackagesConfig.Count);
                Assert.Equal(0, msBuildNuGetProjectSystem.References.Count);

                // Act
                await nuGetPackageManager.InstallPackageAsync(msBuildNuGetProject, packageIdentity,
                    resolutionContext, testNuGetProjectContext, sourceRepositoryProvider.GetRepositories().First(), null, token);

                // Assert
                // Check that the packages.config file exists after the installation
                Assert.True(File.Exists(packagesConfigPath));
                // Check the number of packages and packages returned by PackagesConfigProject after the installation
                packagesInPackagesConfig = (await msBuildNuGetProject.PackagesConfigNuGetProject.GetInstalledPackagesAsync(token)).ToList();
                Assert.Equal(2, packagesInPackagesConfig.Count);
                Assert.Equal(packageIdentity, packagesInPackagesConfig[1].PackageIdentity);
                Assert.Equal(msBuildNuGetProject.MSBuildNuGetProjectSystem.TargetFramework, packagesInPackagesConfig[1].TargetFramework);
                Assert.Equal(PackageWithDependents[0], packagesInPackagesConfig[0].PackageIdentity);
                Assert.Equal(msBuildNuGetProject.MSBuildNuGetProjectSystem.TargetFramework, packagesInPackagesConfig[0].TargetFramework);

                // Main Act
                Exception exception = null;
                try
                {
                    var uninstallationContext = new UninstallationContext();
                    var packageActions = await nuGetPackageManager.PreviewUninstallPackageAsync(msBuildNuGetProject, "jQuery",
                        uninstallationContext, testNuGetProjectContext, token);
                }
                catch (InvalidOperationException ex)
                {
                    exception = ex;
                }
                catch (AggregateException ex)
                {
                    exception = ExceptionUtility.Unwrap(ex);
                }

                Assert.NotNull(exception);
                Assert.True(exception is InvalidOperationException);
                Assert.Equal("Unable to uninstall 'jQuery.1.4.4' because 'jQuery.Validation.1.13.1' depends on it.",
                    exception.Message);
            }
        }

        [Fact]
        public async Task TestPacManUninstallPackageOnMultipleProjects()
        {
            // Arrange
            var sourceRepositoryProvider = TestSourceRepositoryUtility.CreateV3OnlySourceRepositoryProvider();
            using (var testSolutionManager = new TestSolutionManager(true))
            {
                var testSettings = new Configuration.NullSettings();
                var token = CancellationToken.None;
                var resolutionContext = new ResolutionContext();
                var testNuGetProjectContext = new TestNuGetProjectContext();
                var deleteOnRestartManager = new TestDeleteOnRestartManager();
                var nuGetPackageManager = new NuGetPackageManager(
                    sourceRepositoryProvider,
                    testSettings,
                    testSolutionManager,
                    deleteOnRestartManager);
                var packagesFolderPath = PackagesFolderPathUtility.GetPackagesFolderPath(testSolutionManager, testSettings);
                var packagePathResolver = new PackagePathResolver(packagesFolderPath);

                var projectA = testSolutionManager.AddNewMSBuildProject();
                var projectB = testSolutionManager.AddNewMSBuildProject();
                var packageIdentity = NoDependencyLibPackages[0];

                // Act
                await nuGetPackageManager.InstallPackageAsync(projectA, packageIdentity,
                    resolutionContext, testNuGetProjectContext, sourceRepositoryProvider.GetRepositories().First(), null, token);
                await nuGetPackageManager.InstallPackageAsync(projectB, packageIdentity,
                    resolutionContext, testNuGetProjectContext, sourceRepositoryProvider.GetRepositories().First(), null, token);

                // Assert
                var projectAInstalled = (await projectA.GetInstalledPackagesAsync(token)).ToList();
                var projectBInstalled = (await projectB.GetInstalledPackagesAsync(token)).ToList();
                Assert.Equal(1, projectAInstalled.Count);
                Assert.Equal(1, projectBInstalled.Count);

                // Main Act
                var uninstallationContext = new UninstallationContext();
                await nuGetPackageManager.UninstallPackageAsync(projectA, packageIdentity.Id,
                    uninstallationContext, testNuGetProjectContext, token);

                // Assert
                projectAInstalled = (await projectA.GetInstalledPackagesAsync(token)).ToList();
                projectBInstalled = (await projectB.GetInstalledPackagesAsync(token)).ToList();
                Assert.Equal(0, projectAInstalled.Count);
                Assert.Equal(1, projectBInstalled.Count);
                Assert.True(File.Exists(packagePathResolver.GetInstalledPackageFilePath(packageIdentity)));
            }
        }

        [Fact]
        public async Task TestPacManInstallHigherSpecificVersion()
        {
            // Arrange
            var sourceRepositoryProvider = TestSourceRepositoryUtility.CreateV3OnlySourceRepositoryProvider();
            using (var testSolutionManager = new TestSolutionManager(true))
            {
                var testSettings = new Configuration.NullSettings();
                var token = CancellationToken.None;
                var resolutionContext = new ResolutionContext();
                var testNuGetProjectContext = new TestNuGetProjectContext();
                var deleteOnRestartManager = new TestDeleteOnRestartManager();
                var nuGetPackageManager = new NuGetPackageManager(
                    sourceRepositoryProvider,
                    testSettings,
                    testSolutionManager,
                    deleteOnRestartManager);
                var packagesFolderPath = PackagesFolderPathUtility.GetPackagesFolderPath(testSolutionManager, testSettings);
                var packagePathResolver = new PackagePathResolver(packagesFolderPath);

                var projectA = testSolutionManager.AddNewMSBuildProject();
                var packageIdentity0 = PackageWithDependents[0];
                var packageIdentity1 = PackageWithDependents[1];

                // Act
                await nuGetPackageManager.InstallPackageAsync(projectA, packageIdentity0,
                    resolutionContext, testNuGetProjectContext, sourceRepositoryProvider.GetRepositories().First(), null, token);

                // Assert
                var projectAInstalled = (await projectA.GetInstalledPackagesAsync(token)).ToList();
                Assert.Equal(1, projectAInstalled.Count);
                Assert.Equal(packageIdentity0, projectAInstalled[0].PackageIdentity);
                Assert.True(File.Exists(packagePathResolver.GetInstalledPackageFilePath(packageIdentity0)));
                Assert.False(File.Exists(packagePathResolver.GetInstalledPackageFilePath(packageIdentity1)));

                // Main Act
                await nuGetPackageManager.InstallPackageAsync(projectA, packageIdentity1,
                    resolutionContext, testNuGetProjectContext, sourceRepositoryProvider.GetRepositories().First(), null, token);

                // Assert
                projectAInstalled = (await projectA.GetInstalledPackagesAsync(token)).ToList();
                Assert.Equal(1, projectAInstalled.Count);
                Assert.Equal(packageIdentity1, projectAInstalled[0].PackageIdentity);
                Assert.False(File.Exists(packagePathResolver.GetInstalledPackageFilePath(packageIdentity0)));
                Assert.True(File.Exists(packagePathResolver.GetInstalledPackageFilePath(packageIdentity1)));
            }
        }

        [Fact]
        public async Task TestPacManInstallLowerSpecificVersion()
        {
            // Arrange
            var sourceRepositoryProvider = TestSourceRepositoryUtility.CreateV3OnlySourceRepositoryProvider();
            using (var testSolutionManager = new TestSolutionManager(true))
            {
                var testSettings = new Configuration.NullSettings();
                var token = CancellationToken.None;
                var resolutionContext = new ResolutionContext();
                var testNuGetProjectContext = new TestNuGetProjectContext();
                var deleteOnRestartManager = new TestDeleteOnRestartManager();
                var nuGetPackageManager = new NuGetPackageManager(
                    sourceRepositoryProvider,
                    testSettings,
                    testSolutionManager,
                    deleteOnRestartManager);
                var packagesFolderPath = PackagesFolderPathUtility.GetPackagesFolderPath(testSolutionManager, testSettings);
                var packagePathResolver = new PackagePathResolver(packagesFolderPath);

                var projectA = testSolutionManager.AddNewMSBuildProject();
                var packageIdentity0 = PackageWithDependents[0];
                var packageIdentity1 = PackageWithDependents[1];

                // Act
                await nuGetPackageManager.InstallPackageAsync(projectA, packageIdentity1,
                    resolutionContext, testNuGetProjectContext, sourceRepositoryProvider.GetRepositories().First(), null, token);

                // Assert
                var projectAInstalled = (await projectA.GetInstalledPackagesAsync(token)).ToList();
                Assert.Equal(1, projectAInstalled.Count);
                Assert.Equal(packageIdentity1, projectAInstalled[0].PackageIdentity);
                Assert.True(File.Exists(packagePathResolver.GetInstalledPackageFilePath(packageIdentity1)));
                Assert.False(File.Exists(packagePathResolver.GetInstalledPackageFilePath(packageIdentity0)));

                // Main Act
                await nuGetPackageManager.InstallPackageAsync(projectA, packageIdentity0,
                    resolutionContext, testNuGetProjectContext, sourceRepositoryProvider.GetRepositories().First(), null, token);

                // Assert
                projectAInstalled = (await projectA.GetInstalledPackagesAsync(token)).ToList();
                Assert.Equal(1, projectAInstalled.Count);
                Assert.Equal(packageIdentity0, projectAInstalled[0].PackageIdentity);
                Assert.False(File.Exists(packagePathResolver.GetInstalledPackageFilePath(packageIdentity1)));
                Assert.True(File.Exists(packagePathResolver.GetInstalledPackageFilePath(packageIdentity0)));
            }
        }

        [Fact]
        public async Task TestPacManInstallLatestVersion()
        {
            // Arrange
            var sourceRepositoryProvider = TestSourceRepositoryUtility.CreateV3OnlySourceRepositoryProvider();
            using (var testSolutionManager = new TestSolutionManager(true))
            {
                var testSettings = new Configuration.NullSettings();
                var token = CancellationToken.None;
                var resolutionContext = new ResolutionContext();
                var testNuGetProjectContext = new TestNuGetProjectContext();
                var deleteOnRestartManager = new TestDeleteOnRestartManager();
                var nuGetPackageManager = new NuGetPackageManager(
                    sourceRepositoryProvider,
                    testSettings,
                    testSolutionManager,
                    deleteOnRestartManager);
                var packagesFolderPath = PackagesFolderPathUtility.GetPackagesFolderPath(testSolutionManager, testSettings);
                var packagePathResolver = new PackagePathResolver(packagesFolderPath);

                var projectA = testSolutionManager.AddNewMSBuildProject();
                var packageIdentity0 = PackageWithDependents[0];

                var latestVersion = await NuGetPackageManager.GetLatestVersionAsync(
                    packageIdentity0.Id,
                    projectA,
                    resolutionContext,
                    sourceRepositoryProvider.GetRepositories().First(),
                    Common.NullLogger.Instance,
                    token);

                var packageLatest = new PackageIdentity(packageIdentity0.Id, latestVersion);

                // Act
                await nuGetPackageManager.InstallPackageAsync(projectA, packageIdentity0,
                    resolutionContext, testNuGetProjectContext, sourceRepositoryProvider.GetRepositories().First(), null, token);

                // Assert
                var projectAInstalled = (await projectA.GetInstalledPackagesAsync(token)).ToList();
                Assert.Equal(1, projectAInstalled.Count);
                Assert.Equal(packageIdentity0, projectAInstalled[0].PackageIdentity);
                Assert.True(File.Exists(packagePathResolver.GetInstalledPackageFilePath(packageIdentity0)));
                Assert.False(File.Exists(packagePathResolver.GetInstalledPackageFilePath(packageLatest)));

                // Main Act
                await nuGetPackageManager.InstallPackageAsync(projectA, packageIdentity0.Id,
                    resolutionContext, testNuGetProjectContext, sourceRepositoryProvider.GetRepositories().First(), null, token);

                // Assert
                projectAInstalled = (await projectA.GetInstalledPackagesAsync(token)).ToList();
                Assert.Equal(1, projectAInstalled.Count);
                Assert.Equal(packageLatest, projectAInstalled[0].PackageIdentity);
                Assert.False(File.Exists(packagePathResolver.GetInstalledPackageFilePath(packageIdentity0)));
                Assert.True(File.Exists(packagePathResolver.GetInstalledPackageFilePath(packageLatest)));
            }
        }

        [Fact]
        public async Task TestPacManInstallLatestVersionOfDependencyPackage()
        {
            // Arrange
            var sourceRepositoryProvider = TestSourceRepositoryUtility.CreateV3OnlySourceRepositoryProvider();
            using (var testSolutionManager = new TestSolutionManager(true))
            {
                var testSettings = new Configuration.NullSettings();
                var token = CancellationToken.None;
                var resolutionContext = new ResolutionContext();
                var testNuGetProjectContext = new TestNuGetProjectContext();
                var deleteOnRestartManager = new TestDeleteOnRestartManager();
                var nuGetPackageManager = new NuGetPackageManager(
                    sourceRepositoryProvider,
                    testSettings,
                    testSolutionManager,
                    deleteOnRestartManager);
                var packagesFolderPath = PackagesFolderPathUtility.GetPackagesFolderPath(testSolutionManager, testSettings);
                var packagePathResolver = new PackagePathResolver(packagesFolderPath);

                var projectA = testSolutionManager.AddNewMSBuildProject();
                var packageIdentity0 = PackageWithDependents[0];
                var dependentPackage = PackageWithDependents[2];

                var latestVersion = await NuGetPackageManager.GetLatestVersionAsync(
                    packageIdentity0.Id,
                    projectA,
                    resolutionContext,
                    sourceRepositoryProvider.GetRepositories().First(),
                    Common.NullLogger.Instance,
                    token);

                var packageLatest = new PackageIdentity(packageIdentity0.Id, latestVersion);

                // Act
                await nuGetPackageManager.InstallPackageAsync(projectA, dependentPackage,
                    resolutionContext, testNuGetProjectContext, sourceRepositoryProvider.GetRepositories().First(), null, token);

                // Assert
                var projectAInstalled = (await projectA.GetInstalledPackagesAsync(token)).ToList();
                Assert.Equal(2, projectAInstalled.Count);
                Assert.Equal(packageIdentity0, projectAInstalled[0].PackageIdentity);
                Assert.True(File.Exists(packagePathResolver.GetInstalledPackageFilePath(packageIdentity0)));
                Assert.Equal(dependentPackage, projectAInstalled[1].PackageIdentity);
                Assert.True(File.Exists(packagePathResolver.GetInstalledPackageFilePath(dependentPackage)));
                Assert.False(File.Exists(packagePathResolver.GetInstalledPackageFilePath(packageLatest)));

                // Main Act
                await nuGetPackageManager.InstallPackageAsync(projectA, packageIdentity0.Id,
                    resolutionContext, testNuGetProjectContext, sourceRepositoryProvider.GetRepositories().First(), null, token);

                // Assert
                projectAInstalled = (await projectA.GetInstalledPackagesAsync(token)).ToList();
                Assert.Equal(2, projectAInstalled.Count);
                Assert.Equal(packageLatest, projectAInstalled[0].PackageIdentity);
                Assert.Equal(dependentPackage, projectAInstalled[1].PackageIdentity);
                Assert.True(File.Exists(packagePathResolver.GetInstalledPackageFilePath(dependentPackage)));
                Assert.False(File.Exists(packagePathResolver.GetInstalledPackageFilePath(packageIdentity0)));
                Assert.True(File.Exists(packagePathResolver.GetInstalledPackageFilePath(packageLatest)));
            }
        }

        [Fact]
        public async Task TestPacManInstallHigherSpecificVersionOfDependencyPackage()
        {
            // Arrange
            var sourceRepositoryProvider = TestSourceRepositoryUtility.CreateV3OnlySourceRepositoryProvider();
            using (var testSolutionManager = new TestSolutionManager(true))
            {
                var testSettings = new Configuration.NullSettings();
                var token = CancellationToken.None;
                var resolutionContext = new ResolutionContext();
                var testNuGetProjectContext = new TestNuGetProjectContext();
                var deleteOnRestartManager = new TestDeleteOnRestartManager();
                var nuGetPackageManager = new NuGetPackageManager(
                    sourceRepositoryProvider,
                    testSettings,
                    testSolutionManager,
                    deleteOnRestartManager);
                var packagesFolderPath = PackagesFolderPathUtility.GetPackagesFolderPath(testSolutionManager, testSettings);
                var packagePathResolver = new PackagePathResolver(packagesFolderPath);

                var projectA = testSolutionManager.AddNewMSBuildProject();
                var packageIdentity0 = PackageWithDependents[0];
                var packageIdentity1 = PackageWithDependents[1];
                var dependentPackage = PackageWithDependents[2];

                // Act
                await nuGetPackageManager.InstallPackageAsync(projectA, dependentPackage,
                    resolutionContext, testNuGetProjectContext, sourceRepositoryProvider.GetRepositories().First(), null, token);

                // Assert
                var projectAInstalled = (await projectA.GetInstalledPackagesAsync(token)).ToList();
                Assert.Equal(2, projectAInstalled.Count);
                Assert.Equal(packageIdentity0, projectAInstalled[0].PackageIdentity);
                Assert.True(File.Exists(packagePathResolver.GetInstalledPackageFilePath(packageIdentity0)));
                Assert.Equal(dependentPackage, projectAInstalled[1].PackageIdentity);
                Assert.True(File.Exists(packagePathResolver.GetInstalledPackageFilePath(dependentPackage)));
                Assert.False(File.Exists(packagePathResolver.GetInstalledPackageFilePath(packageIdentity1)));

                // Main Act
                await nuGetPackageManager.InstallPackageAsync(projectA, packageIdentity1,
                    resolutionContext, testNuGetProjectContext, sourceRepositoryProvider.GetRepositories().First(), null, token);

                // Assert
                projectAInstalled = (await projectA.GetInstalledPackagesAsync(token)).ToList();
                Assert.Equal(2, projectAInstalled.Count);
                Assert.Equal(packageIdentity1, projectAInstalled[0].PackageIdentity);
                Assert.Equal(dependentPackage, projectAInstalled[1].PackageIdentity);
                Assert.True(File.Exists(packagePathResolver.GetInstalledPackageFilePath(dependentPackage)));
                Assert.False(File.Exists(packagePathResolver.GetInstalledPackageFilePath(packageIdentity0)));
                Assert.True(File.Exists(packagePathResolver.GetInstalledPackageFilePath(packageIdentity1)));
            }
        }

        [Fact]
        public async Task TestPacManInstallLowerSpecificVersionOfDependencyPackage()
        {
            // Arrange
            var sourceRepositoryProvider = TestSourceRepositoryUtility.CreateV3OnlySourceRepositoryProvider();
            using (var testSolutionManager = new TestSolutionManager(true))
            {
                var testSettings = new Configuration.NullSettings();
                var token = CancellationToken.None;
                var resolutionContext = new ResolutionContext(DependencyBehavior.Highest, false, true, VersionConstraints.None);
                var testNuGetProjectContext = new TestNuGetProjectContext();
                var deleteOnRestartManager = new TestDeleteOnRestartManager();
                var nuGetPackageManager = new NuGetPackageManager(
                    sourceRepositoryProvider,
                    testSettings,
                    testSolutionManager,
                    deleteOnRestartManager);
                var packagesFolderPath = PackagesFolderPathUtility.GetPackagesFolderPath(testSolutionManager, testSettings);
                var packagePathResolver = new PackagePathResolver(packagesFolderPath);

                var projectA = testSolutionManager.AddNewMSBuildProject();
                var packageIdentity0 = PackageWithDependents[0];
                var dependentPackage = PackageWithDependents[2];

                var latestVersion = await NuGetPackageManager.GetLatestVersionAsync(
                    packageIdentity0.Id,
                    projectA,
                    resolutionContext,
                    sourceRepositoryProvider.GetRepositories().First(),
                    Common.NullLogger.Instance,
                    token);

                var packageLatest = new PackageIdentity(packageIdentity0.Id, latestVersion);

                // Act
                await nuGetPackageManager.InstallPackageAsync(projectA, dependentPackage,
                    resolutionContext, testNuGetProjectContext, sourceRepositoryProvider.GetRepositories().First(), null, token);

                // Assert
                var projectAInstalled = (await projectA.GetInstalledPackagesAsync(token)).ToList();
                Assert.Equal(2, projectAInstalled.Count);
                Assert.Equal(packageLatest, projectAInstalled[0].PackageIdentity);
                Assert.True(File.Exists(packagePathResolver.GetInstalledPackageFilePath(packageLatest)));
                Assert.Equal(dependentPackage, projectAInstalled[1].PackageIdentity);
                Assert.True(File.Exists(packagePathResolver.GetInstalledPackageFilePath(dependentPackage)));
                Assert.False(File.Exists(packagePathResolver.GetInstalledPackageFilePath(packageIdentity0)));

                // Main Act
                await nuGetPackageManager.InstallPackageAsync(projectA, packageIdentity0,
                    resolutionContext, testNuGetProjectContext, sourceRepositoryProvider.GetRepositories().First(), null, token);

                // Assert
                projectAInstalled = (await projectA.GetInstalledPackagesAsync(token)).ToList();
                Assert.Equal(2, projectAInstalled.Count);
                Assert.Equal(packageIdentity0, projectAInstalled[0].PackageIdentity);
                Assert.Equal(dependentPackage, projectAInstalled[1].PackageIdentity);
                Assert.True(File.Exists(packagePathResolver.GetInstalledPackageFilePath(dependentPackage)));
                Assert.False(File.Exists(packagePathResolver.GetInstalledPackageFilePath(packageLatest)));
                Assert.True(File.Exists(packagePathResolver.GetInstalledPackageFilePath(packageIdentity0)));
            }
        }

        [Fact]
        public async Task TestPacManInstallPackageWhichUpdatesParent()
        {
            // https://github.com/NuGet/Home/issues/127
            // Repro step:
            // 1.Install-Package jquery.validation -Version 1.8
            // 2.Update-package jquery -version 2.0.3
            // Expected: jquery.validation was updated to 1.8.0.1
            // jquery 1.8 is unique because it allows only a single version of jquery

            // Arrange
            var sourceRepositoryProvider = TestSourceRepositoryUtility.CreateV3OnlySourceRepositoryProvider();
            using (var testSolutionManager = new TestSolutionManager(true))
            {
                var testSettings = new Configuration.NullSettings();
                var token = CancellationToken.None;
                var resolutionContext = new ResolutionContext();
                var testNuGetProjectContext = new TestNuGetProjectContext();
                var deleteOnRestartManager = new TestDeleteOnRestartManager();
                var nuGetPackageManager = new NuGetPackageManager(
                    sourceRepositoryProvider,
                    testSettings,
                    testSolutionManager,
                    deleteOnRestartManager);
                var packagesFolderPath = PackagesFolderPathUtility.GetPackagesFolderPath(testSolutionManager, testSettings);
                var packagePathResolver = new PackagePathResolver(packagesFolderPath);

                var projectA = testSolutionManager.AddNewMSBuildProject();
                var jqueryValidation18 = new PackageIdentity("jquery.validation", NuGetVersion.Parse("1.8"));
                var jquery203 = new PackageIdentity("jquery", NuGetVersion.Parse("2.0.3"));

                // Act
                await nuGetPackageManager.InstallPackageAsync(projectA, jqueryValidation18,
                    resolutionContext, testNuGetProjectContext, sourceRepositoryProvider.GetRepositories().First(), null, token);

                // Assert
                var projectAInstalled = (await projectA.GetInstalledPackagesAsync(token)).ToList();
                Assert.Equal(2, projectAInstalled.Count);
                Assert.Equal(jqueryValidation18, projectAInstalled[1].PackageIdentity);
                Assert.True(File.Exists(packagePathResolver.GetInstalledPackageFilePath(jqueryValidation18)));

                // Main Act
                await nuGetPackageManager.InstallPackageAsync(projectA, jquery203,
                    resolutionContext, testNuGetProjectContext, sourceRepositoryProvider.GetRepositories().First(), null, token);

                // Assert
                projectAInstalled = (await projectA.GetInstalledPackagesAsync(token)).ToList();
                Assert.Equal(2, projectAInstalled.Count);
                Assert.Equal(new PackageIdentity("jquery.validation", NuGetVersion.Parse("1.8.0.1")), projectAInstalled[1].PackageIdentity);
                Assert.False(File.Exists(packagePathResolver.GetInstalledPackageFilePath(jqueryValidation18)));
            }
        }

        [Fact]
        public async Task TestPacManInstallPackageWhichUpdatesDependency()
        {
            // Arrange
            var sourceRepositoryProvider = TestSourceRepositoryUtility.CreateV3OnlySourceRepositoryProvider();
            using (var testSolutionManager = new TestSolutionManager(true))
            {
                var testSettings = new Configuration.NullSettings();
                var token = CancellationToken.None;
                var resolutionContext = new ResolutionContext();
                var testNuGetProjectContext = new TestNuGetProjectContext();
                var deleteOnRestartManager = new TestDeleteOnRestartManager();
                var nuGetPackageManager = new NuGetPackageManager(
                    sourceRepositoryProvider,
                    testSettings,
                    testSolutionManager,
                    deleteOnRestartManager);
                var packagesFolderPath = PackagesFolderPathUtility.GetPackagesFolderPath(testSolutionManager, testSettings);
                var packagePathResolver = new PackagePathResolver(packagesFolderPath);

                var projectA = testSolutionManager.AddNewMSBuildProject();
                var packageIdentity0 = PackageWithDependents[0];
                var packageIdentity1 = PackageWithDependents[1];
                var packageIdentity2 = PackageWithDependents[2];
                var packageIdentity3 = PackageWithDependents[3];

                // Act
                await nuGetPackageManager.InstallPackageAsync(projectA, packageIdentity2,
                    resolutionContext, testNuGetProjectContext, sourceRepositoryProvider.GetRepositories().First(), null, token);

                // Assert
                var projectAInstalled = (await projectA.GetInstalledPackagesAsync(token)).ToList();
                Assert.Equal(2, projectAInstalled.Count);
                Assert.Equal(packageIdentity0, projectAInstalled[0].PackageIdentity);
                Assert.Equal(packageIdentity2, projectAInstalled[1].PackageIdentity);
                Assert.True(File.Exists(packagePathResolver.GetInstalledPackageFilePath(packageIdentity0)));
                Assert.True(File.Exists(packagePathResolver.GetInstalledPackageFilePath(packageIdentity2)));

                // Main Act
                await nuGetPackageManager.InstallPackageAsync(projectA, packageIdentity3,
                    resolutionContext, testNuGetProjectContext, sourceRepositoryProvider.GetRepositories().First(), null, token);

                // Assert
                projectAInstalled = (await projectA.GetInstalledPackagesAsync(token)).ToList();
                Assert.Equal(3, projectAInstalled.Count);
                Assert.Equal(packageIdentity1, projectAInstalled[0].PackageIdentity);
                Assert.Equal(packageIdentity2, projectAInstalled[2].PackageIdentity);
                Assert.Equal(packageIdentity3, projectAInstalled[1].PackageIdentity);
                Assert.False(File.Exists(packagePathResolver.GetInstalledPackageFilePath(packageIdentity0)));
                Assert.True(File.Exists(packagePathResolver.GetInstalledPackageFilePath(packageIdentity1)));
                Assert.True(File.Exists(packagePathResolver.GetInstalledPackageFilePath(packageIdentity2)));
                Assert.True(File.Exists(packagePathResolver.GetInstalledPackageFilePath(packageIdentity3)));
            }
        }

        [Fact]
        public async Task TestPacManPreviewUpdatePackageFollowingForceUninstall()
        {
            // Arrange

            // Set up Package Source
            var packages = new List<SourcePackageDependencyInfo>
            {
                new SourcePackageDependencyInfo("a", new NuGetVersion(1, 0, 0), new Packaging.Core.PackageDependency[] { }, true, null),
                new SourcePackageDependencyInfo("a", new NuGetVersion(2, 0, 0), new Packaging.Core.PackageDependency[] { }, true, null),
                new SourcePackageDependencyInfo("a", new NuGetVersion(3, 0, 0), new Packaging.Core.PackageDependency[] { }, true, null),
                new SourcePackageDependencyInfo("b", new NuGetVersion(1, 0, 0), new[] { new Packaging.Core.PackageDependency("a", new VersionRange(new NuGetVersion(1, 0, 0))) }, true, null),
                new SourcePackageDependencyInfo("b", new NuGetVersion(2, 0, 0), new[] { new Packaging.Core.PackageDependency("a", new VersionRange(new NuGetVersion(2, 0, 0))) }, true, null),
                new SourcePackageDependencyInfo("b", new NuGetVersion(3, 0, 0), new[] { new Packaging.Core.PackageDependency("a", new VersionRange(new NuGetVersion(2, 0, 0))) }, true, null),
                new SourcePackageDependencyInfo("c", new NuGetVersion(1, 0, 0), new Packaging.Core.PackageDependency[] { }, true, null),
                new SourcePackageDependencyInfo("c", new NuGetVersion(2, 0, 0), new Packaging.Core.PackageDependency[] { }, true, null),
                new SourcePackageDependencyInfo("c", new NuGetVersion(3, 0, 0), new Packaging.Core.PackageDependency[] { }, true, null),
                new SourcePackageDependencyInfo("d", new NuGetVersion(1, 0, 0), new[] { new Packaging.Core.PackageDependency("e", new VersionRange(new NuGetVersion(1, 0, 0))) }, true, null),
                new SourcePackageDependencyInfo("d", new NuGetVersion(2, 0, 0), new[] { new Packaging.Core.PackageDependency("e", new VersionRange(new NuGetVersion(1, 0, 0))) }, true, null),
                new SourcePackageDependencyInfo("d", new NuGetVersion(3, 0, 0), new[] { new Packaging.Core.PackageDependency("e", new VersionRange(new NuGetVersion(1, 0, 0))) }, true, null),
                new SourcePackageDependencyInfo("d", new NuGetVersion(4, 0, 0), new[] { new Packaging.Core.PackageDependency("e", new VersionRange(new NuGetVersion(1, 0, 0))) }, true, null),
                new SourcePackageDependencyInfo("e", new NuGetVersion(1, 0, 0), new Packaging.Core.PackageDependency[] { }, true, null),
                new SourcePackageDependencyInfo("e", new NuGetVersion(2, 0, 0), new Packaging.Core.PackageDependency[] { }, true, null),
                new SourcePackageDependencyInfo("f", new NuGetVersion(1, 0, 0), new Packaging.Core.PackageDependency[] { }, true, null),
                new SourcePackageDependencyInfo("f", new NuGetVersion(2, 0, 0), new Packaging.Core.PackageDependency[] { }, true, null),
                new SourcePackageDependencyInfo("f", new NuGetVersion(3, 0, 0), new Packaging.Core.PackageDependency[] { }, true, null),
                new SourcePackageDependencyInfo("f", new NuGetVersion(4, 0, 0), new Packaging.Core.PackageDependency[] { }, true, null),
            };

            var sourceRepositoryProvider = CreateSource(packages);

            // Set up NuGetProject
            var fwk45 = NuGetFramework.Parse("net45");

            var installedPackages = new List<NuGet.Packaging.PackageReference>
            {
                new NuGet.Packaging.PackageReference(new PackageIdentity("a", new NuGetVersion(1, 0, 0)), fwk45, true),
                new NuGet.Packaging.PackageReference(new PackageIdentity("b", new NuGetVersion(1, 0, 0)), fwk45, true),
                new NuGet.Packaging.PackageReference(new PackageIdentity("c", new NuGetVersion(2, 0, 0)), fwk45, true),
                new NuGet.Packaging.PackageReference(new PackageIdentity("d", new NuGetVersion(2, 0, 0)), fwk45, true),
                // No package "e" even though "d" depends on it (the user must have done an uninstall-package with a -force option)
            };

            var nuGetProject = new TestNuGetProject(installedPackages);

            // Create Package Manager
            using (var solutionManager = new TestSolutionManager(true))
            {
                var nuGetPackageManager = new NuGetPackageManager(
                    sourceRepositoryProvider,
                    new Configuration.NullSettings(),
                    solutionManager,
                    new TestDeleteOnRestartManager());

                // Main Act
                var targets = new List<PackageIdentity>
                  {
                    new PackageIdentity("b", new NuGetVersion(2, 0, 0)),
                    new PackageIdentity("c", new NuGetVersion(3, 0, 0)),
                  };

                var result = await nuGetPackageManager.PreviewUpdatePackagesAsync(
                    targets,
                    nuGetProject,
                    new ResolutionContext(),
                    new TestNuGetProjectContext(),
                    sourceRepositoryProvider.GetRepositories(),
                    sourceRepositoryProvider.GetRepositories(),
                    CancellationToken.None);

                // Assert
                var resulting = result.Select(a => Tuple.Create(a.PackageIdentity, a.NuGetProjectActionType)).ToArray();

                var expected = new List<Tuple<PackageIdentity, NuGetProjectActionType>>();
                Expected(expected, "a", new NuGetVersion(1, 0, 0), new NuGetVersion(2, 0, 0));
                Expected(expected, "b", new NuGetVersion(1, 0, 0), new NuGetVersion(2, 0, 0));
                Expected(expected, "c", new NuGetVersion(2, 0, 0), new NuGetVersion(3, 0, 0));

                Assert.True(Compare(resulting, expected));
            }
        }

        [Fact]
        public async Task TestPacManInstallPackageWhichUsesExistingDependency()
        {
            // Arrange
            var sourceRepositoryProvider = TestSourceRepositoryUtility.CreateV3OnlySourceRepositoryProvider();
            using (var testSolutionManager = new TestSolutionManager(true))
            {
                var testSettings = new Configuration.NullSettings();
                var token = CancellationToken.None;
                var resolutionContext = new ResolutionContext();
                var testNuGetProjectContext = new TestNuGetProjectContext();
                var deleteOnRestartManager = new TestDeleteOnRestartManager();
                var nuGetPackageManager = new NuGetPackageManager(
                    sourceRepositoryProvider,
                    testSettings,
                    testSolutionManager,
                    deleteOnRestartManager);
                var packagesFolderPath = PackagesFolderPathUtility.GetPackagesFolderPath(testSolutionManager, testSettings);
                var packagePathResolver = new PackagePathResolver(packagesFolderPath);

                var projectA = testSolutionManager.AddNewMSBuildProject();
                var packageIdentity0 = PackageWithDependents[0];
                var packageIdentity1 = PackageWithDependents[1];
                var packageIdentity2 = PackageWithDependents[2];
                var packageIdentity3 = PackageWithDependents[3];

                // Act
                await nuGetPackageManager.InstallPackageAsync(projectA, packageIdentity3,
                    resolutionContext, testNuGetProjectContext, sourceRepositoryProvider.GetRepositories().First(), null, token);

                // Assert
                var projectAInstalled = (await projectA.GetInstalledPackagesAsync(token)).ToList();
                Assert.Equal(2, projectAInstalled.Count);
                Assert.Equal(packageIdentity1, projectAInstalled[0].PackageIdentity);
                Assert.Equal(packageIdentity3, projectAInstalled[1].PackageIdentity);
                Assert.True(File.Exists(packagePathResolver.GetInstalledPackageFilePath(packageIdentity1)));
                Assert.True(File.Exists(packagePathResolver.GetInstalledPackageFilePath(packageIdentity3)));

                // Main Act
                await nuGetPackageManager.InstallPackageAsync(projectA, packageIdentity2,
                    resolutionContext, testNuGetProjectContext, sourceRepositoryProvider.GetRepositories().First(), null, token);

                // Assert
                projectAInstalled = (await projectA.GetInstalledPackagesAsync(token)).ToList();
                Assert.Equal(3, projectAInstalled.Count);
                Assert.Equal(packageIdentity1, projectAInstalled[0].PackageIdentity);
                Assert.Equal(packageIdentity2, projectAInstalled[2].PackageIdentity);
                Assert.Equal(packageIdentity3, projectAInstalled[1].PackageIdentity);
                Assert.False(File.Exists(packagePathResolver.GetInstalledPackageFilePath(packageIdentity0)));
                Assert.True(File.Exists(packagePathResolver.GetInstalledPackageFilePath(packageIdentity1)));
                Assert.True(File.Exists(packagePathResolver.GetInstalledPackageFilePath(packageIdentity2)));
                Assert.True(File.Exists(packagePathResolver.GetInstalledPackageFilePath(packageIdentity3)));
            }
        }

        [Fact]
        public async Task TestPacManInstallPackageWhichUpdatesExistingDependencyDueToDependencyBehavior()
        {
            // Arrange
            var sourceRepositoryProvider = TestSourceRepositoryUtility.CreateV3OnlySourceRepositoryProvider();
            using (var testSolutionManager = new TestSolutionManager(true))
            {
                var testSettings = new Configuration.NullSettings();
                var token = CancellationToken.None;
                var resolutionContext = new ResolutionContext(DependencyBehavior.Highest, false, true, VersionConstraints.None);
                var testNuGetProjectContext = new TestNuGetProjectContext();
                var deleteOnRestartManager = new TestDeleteOnRestartManager();
                var nuGetPackageManager = new NuGetPackageManager(
                    sourceRepositoryProvider,
                    testSettings,
                    testSolutionManager,
                    deleteOnRestartManager);
                var packagesFolderPath = PackagesFolderPathUtility.GetPackagesFolderPath(testSolutionManager, testSettings);
                var packagePathResolver = new PackagePathResolver(packagesFolderPath);

                var projectA = testSolutionManager.AddNewMSBuildProject();
                var packageIdentity0 = PackageWithDependents[0];
                var packageIdentity1 = PackageWithDependents[1];
                var packageIdentity2 = PackageWithDependents[2];
                var packageIdentity3 = PackageWithDependents[3];

                var latestVersion = await NuGetPackageManager.GetLatestVersionAsync(
                    packageIdentity0.Id,
                    projectA,
                    resolutionContext,
                    sourceRepositoryProvider.GetRepositories().First(),
                    Common.NullLogger.Instance,
                    token);

                var packageLatest = new PackageIdentity(packageIdentity0.Id, latestVersion);

                // Act
                await nuGetPackageManager.InstallPackageAsync(projectA, packageIdentity3,
                    resolutionContext, testNuGetProjectContext, sourceRepositoryProvider.GetRepositories().First(), null, token);

                // Assert
                var projectAInstalled = (await projectA.GetInstalledPackagesAsync(token)).ToList();
                Assert.Equal(2, projectAInstalled.Count);
                Assert.Equal(packageLatest, projectAInstalled[0].PackageIdentity);
                Assert.Equal(packageIdentity3, projectAInstalled[1].PackageIdentity);
                Assert.True(File.Exists(packagePathResolver.GetInstalledPackageFilePath(packageLatest)));
                Assert.True(File.Exists(packagePathResolver.GetInstalledPackageFilePath(packageIdentity3)));

                // Main Act
                await nuGetPackageManager.InstallPackageAsync(projectA, packageIdentity2,
                    resolutionContext, testNuGetProjectContext, sourceRepositoryProvider.GetRepositories().First(), null, token);

                // Assert
                projectAInstalled = (await projectA.GetInstalledPackagesAsync(token)).ToList();
                Assert.Equal(3, projectAInstalled.Count);
                Assert.Equal(packageLatest, projectAInstalled[0].PackageIdentity);
                Assert.Equal(packageIdentity2, projectAInstalled[2].PackageIdentity);
                Assert.Equal(packageIdentity3, projectAInstalled[1].PackageIdentity);
                Assert.True(File.Exists(packagePathResolver.GetInstalledPackageFilePath(packageLatest)));
                Assert.True(File.Exists(packagePathResolver.GetInstalledPackageFilePath(packageIdentity2)));
                Assert.True(File.Exists(packagePathResolver.GetInstalledPackageFilePath(packageIdentity3)));
            }
        }

        [Fact]
        public async Task TestPacManPreviewUninstallWithRemoveDependencies()
        {
            // Arrange
            var sourceRepositoryProvider = TestSourceRepositoryUtility.CreateV3OnlySourceRepositoryProvider();
            using (var testSolutionManager = new TestSolutionManager(true))
            {
                var testSettings = new Configuration.NullSettings();
                var token = CancellationToken.None;
                var resolutionContext = new ResolutionContext();
                var testNuGetProjectContext = new TestNuGetProjectContext();
                var deleteOnRestartManager = new TestDeleteOnRestartManager();
                var nuGetPackageManager = new NuGetPackageManager(
                    sourceRepositoryProvider,
                    testSettings,
                    testSolutionManager,
                    deleteOnRestartManager);
                var packagesFolderPath = PackagesFolderPathUtility.GetPackagesFolderPath(testSolutionManager, testSettings);
                var packagePathResolver = new PackagePathResolver(packagesFolderPath);

                var projectA = testSolutionManager.AddNewMSBuildProject();
                var packageIdentity0 = PackageWithDependents[0];
                var packageIdentity1 = PackageWithDependents[1];
                var packageIdentity2 = PackageWithDependents[2];
                var packageIdentity3 = PackageWithDependents[3];

                // Act
                await nuGetPackageManager.InstallPackageAsync(projectA, packageIdentity2,
                    resolutionContext, testNuGetProjectContext, sourceRepositoryProvider.GetRepositories().First(), null, token);

                // Assert
                var projectAInstalled = (await projectA.GetInstalledPackagesAsync(token)).ToList();
                Assert.Equal(2, projectAInstalled.Count);
                Assert.Equal(packageIdentity0, projectAInstalled[0].PackageIdentity);
                Assert.Equal(packageIdentity2, projectAInstalled[1].PackageIdentity);
                Assert.True(File.Exists(packagePathResolver.GetInstalledPackageFilePath(packageIdentity0)));
                Assert.True(File.Exists(packagePathResolver.GetInstalledPackageFilePath(packageIdentity2)));

                // Main Act
                var uninstallationContext = new UninstallationContext(removeDependencies: true);
                var packageActions = (await nuGetPackageManager.PreviewUninstallPackageAsync(projectA,
                    packageIdentity2.Id, uninstallationContext, testNuGetProjectContext, token)).ToList();

                Assert.Equal(2, packageActions.Count);
                Assert.Equal(packageIdentity2, packageActions[0].PackageIdentity);
                Assert.Equal(NuGetProjectActionType.Uninstall, packageActions[0].NuGetProjectActionType);
                Assert.Null(packageActions[0].SourceRepository);
                Assert.Equal(packageIdentity0, packageActions[1].PackageIdentity);
                Assert.Equal(NuGetProjectActionType.Uninstall, packageActions[1].NuGetProjectActionType);
                Assert.Null(packageActions[1].SourceRepository);
            }
        }

        [Fact]
        public async Task TestPacManUninstallWithRemoveDependenciesWithVDependency()
        {
            // Arrange
            var sourceRepositoryProvider = TestSourceRepositoryUtility.CreateV3OnlySourceRepositoryProvider();
            using (var testSolutionManager = new TestSolutionManager(true))
            {
                var testSettings = new Configuration.NullSettings();
                var token = CancellationToken.None;
                var resolutionContext = new ResolutionContext();
                var testNuGetProjectContext = new TestNuGetProjectContext();
                var deleteOnRestartManager = new TestDeleteOnRestartManager();
                var nuGetPackageManager = new NuGetPackageManager(
                    sourceRepositoryProvider,
                    testSettings,
                    testSolutionManager,
                    deleteOnRestartManager);
                var packagesFolderPath = PackagesFolderPathUtility.GetPackagesFolderPath(testSolutionManager, testSettings);
                var packagePathResolver = new PackagePathResolver(packagesFolderPath);

                var projectA = testSolutionManager.AddNewMSBuildProject();
                var packageIdentity0 = PackageWithDependents[0];
                var packageIdentity1 = PackageWithDependents[1];
                var packageIdentity2 = PackageWithDependents[2];
                var packageIdentity3 = PackageWithDependents[3];

                // Act
                await nuGetPackageManager.InstallPackageAsync(projectA, packageIdentity2,
                    resolutionContext, testNuGetProjectContext, sourceRepositoryProvider.GetRepositories().First(), null, token);

                // Assert
                var projectAInstalled = (await projectA.GetInstalledPackagesAsync(token)).ToList();
                Assert.Equal(2, projectAInstalled.Count);
                Assert.Equal(packageIdentity0, projectAInstalled[0].PackageIdentity);
                Assert.Equal(packageIdentity2, projectAInstalled[1].PackageIdentity);
                Assert.True(File.Exists(packagePathResolver.GetInstalledPackageFilePath(packageIdentity0)));
                Assert.True(File.Exists(packagePathResolver.GetInstalledPackageFilePath(packageIdentity2)));

                // Main Act
                await nuGetPackageManager.InstallPackageAsync(projectA, packageIdentity3,
                    resolutionContext, testNuGetProjectContext, sourceRepositoryProvider.GetRepositories().First(), null, token);

                // Assert
                projectAInstalled = (await projectA.GetInstalledPackagesAsync(token)).ToList();
                Assert.Equal(3, projectAInstalled.Count);
                Assert.Equal(packageIdentity1, projectAInstalled[0].PackageIdentity);
                Assert.Equal(packageIdentity2, projectAInstalled[2].PackageIdentity);
                Assert.Equal(packageIdentity3, projectAInstalled[1].PackageIdentity);
                Assert.False(File.Exists(packagePathResolver.GetInstalledPackageFilePath(packageIdentity0)));
                Assert.True(File.Exists(packagePathResolver.GetInstalledPackageFilePath(packageIdentity1)));
                Assert.True(File.Exists(packagePathResolver.GetInstalledPackageFilePath(packageIdentity2)));
                Assert.True(File.Exists(packagePathResolver.GetInstalledPackageFilePath(packageIdentity3)));

                // Main Act
                Exception exception = null;
                try
                {
                    var uninstallationContext = new UninstallationContext(removeDependencies: true);
                    await nuGetPackageManager.UninstallPackageAsync(projectA, packageIdentity2.Id,
                        uninstallationContext, testNuGetProjectContext, token);
                }
                catch (InvalidOperationException ex)
                {
                    exception = ex;
                }
                catch (AggregateException ex)
                {
                    exception = ExceptionUtility.Unwrap(ex);
                }

                Assert.NotNull(exception);
                Assert.True(exception is InvalidOperationException);
                Assert.Equal("Unable to uninstall 'jQuery.1.6.4' because 'jQuery.UI.Combined.1.11.2' depends on it.",
                    exception.Message);
            }
        }

        [Fact]
        public async Task TestPacManUninstallWithForceRemove()
        {
            // Arrange
            var sourceRepositoryProvider = TestSourceRepositoryUtility.CreateV3OnlySourceRepositoryProvider();
            using (var testSolutionManager = new TestSolutionManager(true))
            {
                var testSettings = new Configuration.NullSettings();
                var token = CancellationToken.None;
                var resolutionContext = new ResolutionContext();
                var testNuGetProjectContext = new TestNuGetProjectContext();
                var packagesFolderPath = PackagesFolderPathUtility.GetPackagesFolderPath(testSolutionManager, testSettings);
                var deleteOnRestartManager = new TestDeleteOnRestartManager();
                var nuGetPackageManager = new NuGetPackageManager(
                    sourceRepositoryProvider,
                    testSettings,
                    testSolutionManager,
                    deleteOnRestartManager);

                var msBuildNuGetProject = testSolutionManager.AddNewMSBuildProject();
                var msBuildNuGetProjectSystem = msBuildNuGetProject.MSBuildNuGetProjectSystem as TestMSBuildNuGetProjectSystem;
                var packagesConfigPath = msBuildNuGetProject.PackagesConfigNuGetProject.FullPath;
                var packageIdentity = PackageWithDependents[2];

                // Pre-Assert
                // Check that the packages.config file does not exist
                Assert.False(File.Exists(packagesConfigPath));
                // Check that there are no packages returned by PackagesConfigProject
                var packagesInPackagesConfig = (await msBuildNuGetProject.PackagesConfigNuGetProject.GetInstalledPackagesAsync(token)).ToList();
                Assert.Equal(0, packagesInPackagesConfig.Count);
                Assert.Equal(0, msBuildNuGetProjectSystem.References.Count);

                // Act
                await nuGetPackageManager.InstallPackageAsync(msBuildNuGetProject, packageIdentity,
                    resolutionContext, testNuGetProjectContext, sourceRepositoryProvider.GetRepositories().First(), null, token);

                // Assert
                // Check that the packages.config file exists after the installation
                Assert.True(File.Exists(packagesConfigPath));
                // Check the number of packages and packages returned by PackagesConfigProject after the installation
                packagesInPackagesConfig = (await msBuildNuGetProject.PackagesConfigNuGetProject.GetInstalledPackagesAsync(token)).ToList();
                Assert.Equal(2, packagesInPackagesConfig.Count);
                Assert.Equal(packageIdentity, packagesInPackagesConfig[1].PackageIdentity);
                Assert.Equal(msBuildNuGetProject.MSBuildNuGetProjectSystem.TargetFramework, packagesInPackagesConfig[1].TargetFramework);
                Assert.Equal(PackageWithDependents[0], packagesInPackagesConfig[0].PackageIdentity);
                Assert.Equal(msBuildNuGetProject.MSBuildNuGetProjectSystem.TargetFramework, packagesInPackagesConfig[0].TargetFramework);

                // Main Act
                var uninstallationContext = new UninstallationContext(removeDependencies: false, forceRemove: true);
                await nuGetPackageManager.UninstallPackageAsync(msBuildNuGetProject, "jQuery",
                    uninstallationContext, testNuGetProjectContext, token);

                // Assert
                // Check that the packages.config file exists after the installation
                Assert.True(File.Exists(packagesConfigPath));

                // Check the number of packages and packages returned by PackagesConfigProject after the installation
                packagesInPackagesConfig = (await msBuildNuGetProject.PackagesConfigNuGetProject.GetInstalledPackagesAsync(token)).ToList();
                Assert.Equal(1, packagesInPackagesConfig.Count);
                Assert.Equal(packageIdentity, packagesInPackagesConfig[0].PackageIdentity);
                Assert.Equal(msBuildNuGetProject.MSBuildNuGetProjectSystem.TargetFramework, packagesInPackagesConfig[0].TargetFramework);
            }
        }

        [Fact]
        public async Task TestPacManInstallWithIgnoreDependencies()
        {
            // Arrange
            var sourceRepositoryProvider = TestSourceRepositoryUtility.CreateV3OnlySourceRepositoryProvider();
            using (var testSolutionManager = new TestSolutionManager(true))
            {
                var testSettings = new Configuration.NullSettings();
                var token = CancellationToken.None;
                var deleteOnRestartManager = new TestDeleteOnRestartManager();
                var nuGetPackageManager = new NuGetPackageManager(
                    sourceRepositoryProvider,
                    testSettings,
                    testSolutionManager,
                    deleteOnRestartManager);
                var packagesFolderPath = PackagesFolderPathUtility.GetPackagesFolderPath(testSolutionManager, testSettings);

                var msBuildNuGetProject = testSolutionManager.AddNewMSBuildProject();
                var msBuildNuGetProjectSystem = msBuildNuGetProject.MSBuildNuGetProjectSystem as TestMSBuildNuGetProjectSystem;
                var packagesConfigPath = msBuildNuGetProject.PackagesConfigNuGetProject.FullPath;
                var packageIdentity = PackageWithDependents[2];

                // Pre-Assert
                // Check that the packages.config file does not exist
                Assert.False(File.Exists(packagesConfigPath));
                // Check that there are no packages returned by PackagesConfigProject
                var packagesInPackagesConfig = (await msBuildNuGetProject.PackagesConfigNuGetProject.GetInstalledPackagesAsync(token)).ToList();
                Assert.Equal(0, packagesInPackagesConfig.Count);
                Assert.Equal(0, msBuildNuGetProjectSystem.References.Count);

                // Act
                await nuGetPackageManager.InstallPackageAsync(msBuildNuGetProject, packageIdentity,
                    new ResolutionContext(DependencyBehavior.Ignore, false, true, VersionConstraints.None), new TestNuGetProjectContext(), sourceRepositoryProvider.GetRepositories().First(), null, token);

                // Assert
                // Check that the packages.config file exists after the installation
                Assert.True(File.Exists(packagesConfigPath));
                // Check the number of packages and packages returned by PackagesConfigProject after the installation
                packagesInPackagesConfig = (await msBuildNuGetProject.PackagesConfigNuGetProject.GetInstalledPackagesAsync(token)).ToList();
                Assert.Equal(1, packagesInPackagesConfig.Count);
                Assert.Equal(packageIdentity, packagesInPackagesConfig[0].PackageIdentity);
                Assert.Equal(msBuildNuGetProject.MSBuildNuGetProjectSystem.TargetFramework, packagesInPackagesConfig[0].TargetFramework);
            }
        }

        [Fact]
        public async Task TestPacManThrowsPackageNotFound()
        {
            // Arrange
            var sourceRepositoryProvider = TestSourceRepositoryUtility.CreateV3OnlySourceRepositoryProvider();
            using (var testSolutionManager = new TestSolutionManager(true))
            {
                var testSettings = new Configuration.NullSettings();
                var token = CancellationToken.None;
                var deleteOnRestartManager = new TestDeleteOnRestartManager();
                var nuGetPackageManager = new NuGetPackageManager(
                    sourceRepositoryProvider,
                    testSettings,
                    testSolutionManager,
                    deleteOnRestartManager);
                var packagesFolderPath = PackagesFolderPathUtility.GetPackagesFolderPath(testSolutionManager, testSettings);

                var msBuildNuGetProject = testSolutionManager.AddNewMSBuildProject();
                var msBuildNuGetProjectSystem = msBuildNuGetProject.MSBuildNuGetProjectSystem as TestMSBuildNuGetProjectSystem;
                var packagesConfigPath = msBuildNuGetProject.PackagesConfigNuGetProject.FullPath;
                var packageIdentity = new PackageIdentity("DoesNotExist", new NuGetVersion("1.0.0"));

                // Pre-Assert
                // Check that the packages.config file does not exist
                Assert.False(File.Exists(packagesConfigPath));
                // Check that there are no packages returned by PackagesConfigProject
                var packagesInPackagesConfig = (await msBuildNuGetProject.PackagesConfigNuGetProject.GetInstalledPackagesAsync(token)).ToList();
                Assert.Equal(0, packagesInPackagesConfig.Count);
                Assert.Equal(0, msBuildNuGetProjectSystem.References.Count);

                // Act
                Exception exception = null;
                try
                {
                    await nuGetPackageManager.InstallPackageAsync(msBuildNuGetProject, packageIdentity,
                        new ResolutionContext(), new TestNuGetProjectContext(), sourceRepositoryProvider.GetRepositories().First(), null, token);
                }
                catch (Exception ex)
                {
                    exception = ex;
                }

                Assert.NotNull(exception);
                Assert.True(exception is InvalidOperationException);
                Assert.Contains("Package 'DoesNotExist,1.0.0' is not found", exception.Message);
            }
        }

        [Fact]
        public async Task TestPacManThrowsLatestVersionNotFound()
        {
            // Arrange
            var sourceRepositoryProvider = TestSourceRepositoryUtility.CreateV3OnlySourceRepositoryProvider();
            using (var testSolutionManager = new TestSolutionManager(true))
            {
                var testSettings = new Configuration.NullSettings();
                var token = CancellationToken.None;
                var deleteOnRestartManager = new TestDeleteOnRestartManager();
                var nuGetPackageManager = new NuGetPackageManager(
                    sourceRepositoryProvider,
                    testSettings,
                    testSolutionManager,
                    deleteOnRestartManager);
                var packagesFolderPath = PackagesFolderPathUtility.GetPackagesFolderPath(testSolutionManager, testSettings);

                var msBuildNuGetProject = testSolutionManager.AddNewMSBuildProject();
                var msBuildNuGetProjectSystem = msBuildNuGetProject.MSBuildNuGetProjectSystem as TestMSBuildNuGetProjectSystem;
                var packagesConfigPath = msBuildNuGetProject.PackagesConfigNuGetProject.FullPath;
                var packageIdentity = "DoesNotExist";

                // Pre-Assert
                // Check that the packages.config file does not exist
                Assert.False(File.Exists(packagesConfigPath));
                // Check that there are no packages returned by PackagesConfigProject
                var packagesInPackagesConfig = (await msBuildNuGetProject.PackagesConfigNuGetProject.GetInstalledPackagesAsync(token)).ToList();
                Assert.Equal(0, packagesInPackagesConfig.Count);
                Assert.Equal(0, msBuildNuGetProjectSystem.References.Count);

                // Act
                Exception exception = null;
                try
                {
                    await nuGetPackageManager.InstallPackageAsync(msBuildNuGetProject, packageIdentity,
                        new ResolutionContext(), new TestNuGetProjectContext(), sourceRepositoryProvider.GetRepositories().First(), null, token);
                }
                catch (Exception ex)
                {
                    exception = ex;
                }

                Assert.NotNull(exception);
                Assert.True(exception is InvalidOperationException);
                Assert.Equal("No latest version found for 'DoesNotExist' for the given source repositories and resolution context", exception.Message);
            }
        }

        [Fact]
        public async Task TestPacManInstallPackageWithDeepDependency()
        {
            // Arrange
            var sourceRepositoryProvider = TestSourceRepositoryUtility.CreateV3OnlySourceRepositoryProvider();
            using (var testSolutionManager = new TestSolutionManager(true))
            {
                var testSettings = new Configuration.NullSettings();
                var token = CancellationToken.None;
                var deleteOnRestartManager = new TestDeleteOnRestartManager();
                var nuGetPackageManager = new NuGetPackageManager(
                    sourceRepositoryProvider,
                    testSettings,
                    testSolutionManager,
                    deleteOnRestartManager);
                var packagesFolderPath = PackagesFolderPathUtility.GetPackagesFolderPath(testSolutionManager, testSettings);

                var msBuildNuGetProject = testSolutionManager.AddNewMSBuildProject();
                var msBuildNuGetProjectSystem = msBuildNuGetProject.MSBuildNuGetProjectSystem as TestMSBuildNuGetProjectSystem;
                var packagesConfigPath = msBuildNuGetProject.PackagesConfigNuGetProject.FullPath;
                var packageIdentity = PackageWithDeepDependency[6]; // WindowsAzure.Storage.4.3.0

                // Pre-Assert
                // Check that the packages.config file does not exist
                Assert.False(File.Exists(packagesConfigPath));
                // Check that there are no packages returned by PackagesConfigProject
                var packagesInPackagesConfig = (await msBuildNuGetProject.PackagesConfigNuGetProject.GetInstalledPackagesAsync(token)).ToList();
                Assert.Equal(0, packagesInPackagesConfig.Count);
                Assert.Equal(0, msBuildNuGetProjectSystem.References.Count);

                // Act
                await nuGetPackageManager.InstallPackageAsync(msBuildNuGetProject, packageIdentity,
                    new ResolutionContext(), new TestNuGetProjectContext(), sourceRepositoryProvider.GetRepositories().First(), null, token);

                // Assert
                // Check that the packages.config file exists after the installation
                Assert.True(File.Exists(packagesConfigPath));
                // Check the number of packages and packages returned by PackagesConfigProject after the installation
                packagesInPackagesConfig = (await msBuildNuGetProject.PackagesConfigNuGetProject.GetInstalledPackagesAsync(token)).ToList();
                Assert.Equal(7, packagesInPackagesConfig.Count);

                var installedPackages = PackageWithDeepDependency.OrderBy(f => f.Id).ToList();

                for (var i = 0; i < 7; i++)
                {
                    Assert.Equal(installedPackages[i], packagesInPackagesConfig[i].PackageIdentity);
                    Assert.Equal(msBuildNuGetProject.MSBuildNuGetProjectSystem.TargetFramework, packagesInPackagesConfig[i].TargetFramework);
                }
            }
        }

        [Fact]
        public async Task TestPacManInstallPackageBindingRedirectsWithDeepDependency()
        {
            // Arrange
            var sourceRepositoryProvider = TestSourceRepositoryUtility.CreateV3OnlySourceRepositoryProvider();
            using (var testSolutionManager = new TestSolutionManager(true))
            {
                var testSettings = new Configuration.NullSettings();
                var token = CancellationToken.None;
                var deleteOnRestartManager = new TestDeleteOnRestartManager();
                var nuGetPackageManager = new NuGetPackageManager(
                    sourceRepositoryProvider,
                    testSettings,
                    testSolutionManager,
                    deleteOnRestartManager);
                var packagesFolderPath = PackagesFolderPathUtility.GetPackagesFolderPath(testSolutionManager, testSettings);

                var msBuildNuGetProject = testSolutionManager.AddNewMSBuildProject();
                var msBuildNuGetProjectSystem = msBuildNuGetProject.MSBuildNuGetProjectSystem as TestMSBuildNuGetProjectSystem;
                var packagesConfigPath = msBuildNuGetProject.PackagesConfigNuGetProject.FullPath;
                var packageIdentity = PackageWithDeepDependency[6]; // WindowsAzure.Storage.4.3.0

                // Pre-Assert
                // Check that the packages.config file does not exist
                Assert.False(File.Exists(packagesConfigPath));
                // Check that there are no packages returned by PackagesConfigProject
                var packagesInPackagesConfig = (await msBuildNuGetProject.PackagesConfigNuGetProject.GetInstalledPackagesAsync(token)).ToList();
                Assert.Equal(0, packagesInPackagesConfig.Count);
                Assert.Equal(0, msBuildNuGetProjectSystem.References.Count);

                // Act
                await nuGetPackageManager.InstallPackageAsync(msBuildNuGetProject, packageIdentity,
                    new ResolutionContext(), new TestNuGetProjectContext(), sourceRepositoryProvider.GetRepositories().First(), null, token);

                // Assert
                // Check that the packages.config file exists after the installation
                Assert.True(File.Exists(packagesConfigPath));
                // Check the number of packages and packages returned by PackagesConfigProject after the installation
                packagesInPackagesConfig = (await msBuildNuGetProject.PackagesConfigNuGetProject.GetInstalledPackagesAsync(token)).ToList();
                Assert.Equal(7, packagesInPackagesConfig.Count);
                Assert.Equal(1, msBuildNuGetProjectSystem.BindingRedirectsCallCount);
            }
        }

        [Fact]
        public async Task TestPacManInstallPackageBindingRedirectsDisabledWithDeepDependency()
        {
            // Arrange
            var sourceRepositoryProvider = TestSourceRepositoryUtility.CreateV3OnlySourceRepositoryProvider();
            using (var testSolutionManager = new TestSolutionManager(true))
            {
                var testSettings = new Configuration.NullSettings();
                var token = CancellationToken.None;
                var deleteOnRestartManager = new TestDeleteOnRestartManager();
                var nuGetPackageManager = new NuGetPackageManager(
                    sourceRepositoryProvider,
                    testSettings,
                    testSolutionManager,
                    deleteOnRestartManager);
                var packagesFolderPath = PackagesFolderPathUtility.GetPackagesFolderPath(testSolutionManager, testSettings);

                var msBuildNuGetProject = testSolutionManager.AddNewMSBuildProject();
                var msBuildNuGetProjectSystem = msBuildNuGetProject.MSBuildNuGetProjectSystem as TestMSBuildNuGetProjectSystem;
                var packagesConfigPath = msBuildNuGetProject.PackagesConfigNuGetProject.FullPath;
                var packageIdentity = PackageWithDeepDependency[6]; // WindowsAzure.Storage.4.3.0

                // Pre-Assert
                // Check that the packages.config file does not exist
                Assert.False(File.Exists(packagesConfigPath));
                // Check that there are no packages returned by PackagesConfigProject
                var packagesInPackagesConfig = (await msBuildNuGetProject.PackagesConfigNuGetProject.GetInstalledPackagesAsync(token)).ToList();
                Assert.Equal(0, packagesInPackagesConfig.Count);
                Assert.Equal(0, msBuildNuGetProjectSystem.References.Count);

                // Act
                await nuGetPackageManager.InstallPackageAsync(msBuildNuGetProject, packageIdentity,
                    new ResolutionContext(), new TestNuGetProjectContext { BindingRedirectsDisabled = true }, sourceRepositoryProvider.GetRepositories().First(), null, token);

                // Assert
                // Check that the packages.config file exists after the installation
                Assert.True(File.Exists(packagesConfigPath));
                // Check the number of packages and packages returned by PackagesConfigProject after the installation
                packagesInPackagesConfig = (await msBuildNuGetProject.PackagesConfigNuGetProject.GetInstalledPackagesAsync(token)).ToList();
                Assert.Equal(7, packagesInPackagesConfig.Count);
                Assert.Equal(0, msBuildNuGetProjectSystem.BindingRedirectsCallCount);
            }
        }

        [Fact]
        public Task TestPacManGetInstalledPackagesByDependencyOrder()
        {
            return TestPacManGetInstalledPackagesByDependencyOrder(deletePackages: false);
        }

        [Fact]
        public Task TestPacManGetUnrestoredPackagesByDependencyOrder()
        {
            return TestPacManGetInstalledPackagesByDependencyOrder(deletePackages: true);
        }

        private async Task TestPacManGetInstalledPackagesByDependencyOrder(bool deletePackages)
        {
            // Arrange
            var sourceRepositoryProvider = TestSourceRepositoryUtility.CreateV3OnlySourceRepositoryProvider();
            using (var testSolutionManager = new TestSolutionManager(true))
            {
                var testSettings = new Configuration.NullSettings();
                var token = CancellationToken.None;
                var deleteOnRestartManager = new TestDeleteOnRestartManager();
                var nuGetPackageManager = new NuGetPackageManager(
                    sourceRepositoryProvider,
                    testSettings,
                    testSolutionManager,
                    deleteOnRestartManager);
                var packagesFolderPath = PackagesFolderPathUtility.GetPackagesFolderPath(testSolutionManager, testSettings);

                var msBuildNuGetProject = testSolutionManager.AddNewMSBuildProject();
                var msBuildNuGetProjectSystem = msBuildNuGetProject.MSBuildNuGetProjectSystem as TestMSBuildNuGetProjectSystem;
                var packagesConfigPath = msBuildNuGetProject.PackagesConfigNuGetProject.FullPath;
                var packageIdentity = PackageWithDeepDependency[6]; // WindowsAzure.Storage.4.3.0

                // Pre-Assert
                // Check that the packages.config file does not exist
                Assert.False(File.Exists(packagesConfigPath));
                // Check that there are no packages returned by PackagesConfigProject
                var packagesInPackagesConfig = (await msBuildNuGetProject.PackagesConfigNuGetProject.GetInstalledPackagesAsync(token)).ToList();
                Assert.Equal(0, packagesInPackagesConfig.Count);
                Assert.Equal(0, msBuildNuGetProjectSystem.References.Count);

                // Act
                await nuGetPackageManager.InstallPackageAsync(msBuildNuGetProject, packageIdentity,
                    new ResolutionContext(), new TestNuGetProjectContext(), sourceRepositoryProvider.GetRepositories().First(), null, token);

                if (deletePackages)
                {
                    TestFileSystemUtility.DeleteRandomTestFolder(packagesFolderPath);
                }

                // Assert
                // Check that the packages.config file exists after the installation
                Assert.True(File.Exists(packagesConfigPath));
                // Check the number of packages and packages returned by PackagesConfigProject after the installation
                packagesInPackagesConfig = (await msBuildNuGetProject.PackagesConfigNuGetProject.GetInstalledPackagesAsync(token)).ToList();
                Assert.Equal(7, packagesInPackagesConfig.Count);
                var installedPackages = PackageWithDeepDependency.OrderBy(f => f.Id).ToList();
                for (var i = 0; i < 7; i++)
                {
                    Assert.True(installedPackages[i].Equals(packagesInPackagesConfig[i].PackageIdentity));
                    Assert.Equal(msBuildNuGetProject.MSBuildNuGetProjectSystem.TargetFramework, packagesInPackagesConfig[i].TargetFramework);
                }

                // Main Assert
                var installedPackagesInDependencyOrder = (await nuGetPackageManager.GetInstalledPackagesInDependencyOrder
                    (msBuildNuGetProject, token)).ToList();
                if (deletePackages)
                {
                    Assert.Equal(0, installedPackagesInDependencyOrder.Count);
                }
                else
                {
                    Assert.Equal(7, installedPackagesInDependencyOrder.Count);
                    for (var i = 0; i < 7; i++)
                    {
                        Assert.Equal(PackageWithDeepDependency[i], installedPackagesInDependencyOrder[i], PackageIdentity.Comparer);
                    }
                }
            }
        }

        [Fact]
        public async Task TestPacManPreviewInstallPackageWithDeepDependency()
        {
            // Arrange
            var sourceRepositoryProvider = TestSourceRepositoryUtility.CreateV3OnlySourceRepositoryProvider();
            using (var testSolutionManager = new TestSolutionManager(true))
            {
                var testSettings = new Configuration.NullSettings();
                var deleteOnRestartManager = new TestDeleteOnRestartManager();
                var token = CancellationToken.None;
                var nuGetPackageManager = new NuGetPackageManager(
                    sourceRepositoryProvider,
                    testSettings,
                    testSolutionManager,
                    deleteOnRestartManager);
                var packagesFolderPath = PackagesFolderPathUtility.GetPackagesFolderPath(testSolutionManager, testSettings);

                var msBuildNuGetProject = testSolutionManager.AddNewMSBuildProject();
                var msBuildNuGetProjectSystem = msBuildNuGetProject.MSBuildNuGetProjectSystem as TestMSBuildNuGetProjectSystem;
                var packagesConfigPath = msBuildNuGetProject.PackagesConfigNuGetProject.FullPath;
                var packageIdentity = PackageWithDeepDependency[6]; // WindowsAzure.Storage.4.3.0

                // Pre-Assert
                // Check that the packages.config file does not exist
                Assert.False(File.Exists(packagesConfigPath));
                // Check that there are no packages returned by PackagesConfigProject
                var packagesInPackagesConfig = (await msBuildNuGetProject.PackagesConfigNuGetProject.GetInstalledPackagesAsync(token)).ToList();
                Assert.Equal(0, packagesInPackagesConfig.Count);
                Assert.Equal(0, msBuildNuGetProjectSystem.References.Count);

                // Act
                var packageActions = (await nuGetPackageManager.PreviewInstallPackageAsync(msBuildNuGetProject, packageIdentity,
                    new ResolutionContext(), new TestNuGetProjectContext(), sourceRepositoryProvider.GetRepositories().First(), null, token)).ToList();

                // Assert
                Assert.Equal(7, packageActions.Count);
                var soleSourceRepository = sourceRepositoryProvider.GetRepositories().Single();
                for (var i = 0; i < 7; i++)
                {
                    Assert.Equal(PackageWithDeepDependency[i], packageActions[i].PackageIdentity, PackageIdentity.Comparer);
                    Assert.Equal(NuGetProjectActionType.Install, packageActions[i].NuGetProjectActionType);
                    Assert.Equal(soleSourceRepository.PackageSource.Source,
                        packageActions[i].SourceRepository.PackageSource.Source);
                }
            }
        }

        [Fact]
        public async Task TestPacManPreviewUninstallPackageWithDeepDependency()
        {
            // Arrange
            var sourceRepositoryProvider = TestSourceRepositoryUtility.CreateV3OnlySourceRepositoryProvider();
            using (var testSolutionManager = new TestSolutionManager(true))
            {
                var testSettings = new Configuration.NullSettings();
                var deleteOnRestartManager = new TestDeleteOnRestartManager();
                var token = CancellationToken.None;
                var nuGetPackageManager = new NuGetPackageManager(
                    sourceRepositoryProvider,
                    testSettings,
                    testSolutionManager,
                    deleteOnRestartManager);
                var packagesFolderPath = PackagesFolderPathUtility.GetPackagesFolderPath(testSolutionManager, testSettings);

                var msBuildNuGetProject = testSolutionManager.AddNewMSBuildProject();
                var msBuildNuGetProjectSystem = msBuildNuGetProject.MSBuildNuGetProjectSystem as TestMSBuildNuGetProjectSystem;
                var packagesConfigPath = msBuildNuGetProject.PackagesConfigNuGetProject.FullPath;
                var packageIdentity = PackageWithDeepDependency[6]; // WindowsAzure.Storage.4.3.0

                // Pre-Assert
                // Check that the packages.config file does not exist
                Assert.False(File.Exists(packagesConfigPath));
                // Check that there are no packages returned by PackagesConfigProject
                var packagesInPackagesConfig = (await msBuildNuGetProject.PackagesConfigNuGetProject.GetInstalledPackagesAsync(token)).ToList();
                Assert.Equal(0, packagesInPackagesConfig.Count);
                Assert.Equal(0, msBuildNuGetProjectSystem.References.Count);

                // Act
                await nuGetPackageManager.InstallPackageAsync(msBuildNuGetProject, packageIdentity,
                    new ResolutionContext(), new TestNuGetProjectContext(), sourceRepositoryProvider.GetRepositories().First(), null, token);

                // Assert
                // Check that the packages.config file exists after the installation
                Assert.True(File.Exists(packagesConfigPath));
                // Check the number of packages and packages returned by PackagesConfigProject after the installation
                packagesInPackagesConfig = (await msBuildNuGetProject.PackagesConfigNuGetProject.GetInstalledPackagesAsync(token)).ToList();
                Assert.Equal(7, packagesInPackagesConfig.Count);
                Assert.Equal(packageIdentity, packagesInPackagesConfig[6].PackageIdentity);
                Assert.Equal(msBuildNuGetProject.MSBuildNuGetProjectSystem.TargetFramework, packagesInPackagesConfig[6].TargetFramework);

                // Main Act
                var packageActions = (await nuGetPackageManager.PreviewUninstallPackageAsync(msBuildNuGetProject, PackageWithDeepDependency[6],
                    new UninstallationContext(removeDependencies: true), new TestNuGetProjectContext(), token)).ToList();
                Assert.Equal(7, packageActions.Count);
                var soleSourceRepository = sourceRepositoryProvider.GetRepositories().Single();
                Assert.Equal(PackageWithDeepDependency[6], packageActions[0].PackageIdentity);
                Assert.Equal(NuGetProjectActionType.Uninstall, packageActions[0].NuGetProjectActionType);
                Assert.Equal(PackageWithDeepDependency[2], packageActions[1].PackageIdentity);
                Assert.Equal(NuGetProjectActionType.Uninstall, packageActions[1].NuGetProjectActionType);
                Assert.Equal(PackageWithDeepDependency[5], packageActions[2].PackageIdentity);
                Assert.Equal(NuGetProjectActionType.Uninstall, packageActions[2].NuGetProjectActionType);
                Assert.Equal(PackageWithDeepDependency[4], packageActions[3].PackageIdentity);
                Assert.Equal(NuGetProjectActionType.Uninstall, packageActions[3].NuGetProjectActionType);
                Assert.Equal(PackageWithDeepDependency[1], packageActions[4].PackageIdentity);
                Assert.Equal(NuGetProjectActionType.Uninstall, packageActions[4].NuGetProjectActionType);
                Assert.Equal(PackageWithDeepDependency[3], packageActions[5].PackageIdentity);
                Assert.Equal(NuGetProjectActionType.Uninstall, packageActions[5].NuGetProjectActionType);
                Assert.Equal(PackageWithDeepDependency[0], packageActions[6].PackageIdentity);
                Assert.Equal(NuGetProjectActionType.Uninstall, packageActions[6].NuGetProjectActionType);
            }
        }

        //[Fact]
        public async Task TestPacManInstallPackageTargetingASPNetCore50()
        {
            // Arrange
            var sourceRepositoryProvider = TestSourceRepositoryUtility.CreateV3OnlySourceRepositoryProvider();
            using (var testSolutionManager = new TestSolutionManager(true))
            {
                var testSettings = new Configuration.NullSettings();
                var deleteOnRestartManager = new TestDeleteOnRestartManager();
                var token = CancellationToken.None;
                var nuGetPackageManager = new NuGetPackageManager(
                    sourceRepositoryProvider,
                    testSettings,
                    testSolutionManager,
                    deleteOnRestartManager);
                var packagesFolderPath = PackagesFolderPathUtility.GetPackagesFolderPath(testSolutionManager, testSettings);

                var msBuildNuGetProject = testSolutionManager.AddNewMSBuildProject("projectName", NuGetFramework.Parse("aspenetcore50"));
                var msBuildNuGetProjectSystem = msBuildNuGetProject.MSBuildNuGetProjectSystem as TestMSBuildNuGetProjectSystem;
                var packagesConfigPath = msBuildNuGetProject.PackagesConfigNuGetProject.FullPath;
                var packageIdentity = LatestAspNetPackages[0]; // Microsoft.AspNet.Mvc.6.0.0-beta3

                // Pre-Assert
                // Check that the packages.config file does not exist
                Assert.False(File.Exists(packagesConfigPath));
                // Check that there are no packages returned by PackagesConfigProject
                var packagesInPackagesConfig = (await msBuildNuGetProject.PackagesConfigNuGetProject.GetInstalledPackagesAsync(token)).ToList();
                Assert.Equal(0, packagesInPackagesConfig.Count);
                Assert.Equal(0, msBuildNuGetProjectSystem.References.Count);

                var resolutionContext = new ResolutionContext(DependencyBehavior.Lowest, includePrelease: true, includeUnlisted: true, versionConstraints: VersionConstraints.None);
                // Act
                await nuGetPackageManager.InstallPackageAsync(msBuildNuGetProject, packageIdentity,
                    resolutionContext, new TestNuGetProjectContext(), sourceRepositoryProvider.GetRepositories().First(), null, token);

                // Assert
                // Check that the packages.config file exists after the installation
                Assert.True(File.Exists(packagesConfigPath));
                // Check the number of packages and packages returned by PackagesConfigProject after the installation
                packagesInPackagesConfig = (await msBuildNuGetProject.PackagesConfigNuGetProject.GetInstalledPackagesAsync(token)).ToList();
                Assert.Equal(1, packagesInPackagesConfig.Count);
                Assert.Equal(packageIdentity, packagesInPackagesConfig[0].PackageIdentity);
                Assert.Equal(msBuildNuGetProject.MSBuildNuGetProjectSystem.TargetFramework, packagesInPackagesConfig[0].TargetFramework);
            }
        }

        [Fact]
        public async Task TestPacManInstallMvcTargetingNet45()
        {
            // Arrange
            var sourceRepositoryProvider = TestSourceRepositoryUtility.CreateV3OnlySourceRepositoryProvider();
            using (var testSolutionManager = new TestSolutionManager(true))
            {
                var testSettings = new Configuration.NullSettings();
                var token = CancellationToken.None;
                var deleteOnRestartManager = new TestDeleteOnRestartManager();
                var nuGetPackageManager = new NuGetPackageManager(
                    sourceRepositoryProvider,
                    testSettings,
                    testSolutionManager,
                    deleteOnRestartManager);
                var packagesFolderPath = PackagesFolderPathUtility.GetPackagesFolderPath(testSolutionManager, testSettings);

                var msBuildNuGetProject = testSolutionManager.AddNewMSBuildProject();
                var msBuildNuGetProjectSystem = msBuildNuGetProject.MSBuildNuGetProjectSystem as TestMSBuildNuGetProjectSystem;
                var packagesConfigPath = msBuildNuGetProject.PackagesConfigNuGetProject.FullPath;
                var packageIdentity = LatestAspNetPackages[0]; // Microsoft.AspNet.Mvc.6.0.0-beta3

                // Pre-Assert
                // Check that the packages.config file does not exist
                Assert.False(File.Exists(packagesConfigPath));
                // Check that there are no packages returned by PackagesConfigProject
                var packagesInPackagesConfig = (await msBuildNuGetProject.PackagesConfigNuGetProject.GetInstalledPackagesAsync(token)).ToList();
                Assert.Equal(0, packagesInPackagesConfig.Count);
                Assert.Equal(0, msBuildNuGetProjectSystem.References.Count);

                var resolutionContext = new ResolutionContext(DependencyBehavior.Lowest, includePrelease: true, includeUnlisted: true, versionConstraints: VersionConstraints.None);

                // Act
                var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                    async () => await nuGetPackageManager.InstallPackageAsync(
                        msBuildNuGetProject, packageIdentity, resolutionContext, new TestNuGetProjectContext(), sourceRepositoryProvider.GetRepositories().First(), null, token));

                var errorMessage = string.Format(CultureInfo.CurrentCulture,
                    Strings.UnableToFindCompatibleItems, packageIdentity.Id + " " + packageIdentity.Version.ToNormalizedString(), msBuildNuGetProject.MSBuildNuGetProjectSystem.TargetFramework);
                Assert.Equal(errorMessage, exception.Message);

            }
        }

        [Fact]
        public async Task TestPacManPreviewUpdatePackagesSimple()
        {
            // Arrange
            var sourceRepositoryProvider = TestSourceRepositoryUtility.CreateV2OnlySourceRepositoryProvider();
            using (var testSolutionManager = new TestSolutionManager(true))
            {
                var testSettings = new Configuration.NullSettings();
                var token = CancellationToken.None;
                var deleteOnRestartManager = new TestDeleteOnRestartManager();
                var nuGetPackageManager = new NuGetPackageManager(
                    sourceRepositoryProvider,
                    testSettings,
                    testSolutionManager,
                    deleteOnRestartManager);
                var packagesFolderPath = PackagesFolderPathUtility.GetPackagesFolderPath(testSolutionManager, testSettings);

                var msBuildNuGetProject = testSolutionManager.AddNewMSBuildProject();
                var msBuildNuGetProjectSystem = msBuildNuGetProject.MSBuildNuGetProjectSystem as TestMSBuildNuGetProjectSystem;
                var packagesConfigPath = msBuildNuGetProject.PackagesConfigNuGetProject.FullPath;
                var packageIdentity0 = PackageWithDependents[0]; // jQuery.1.4.4

                var resolutionContext = new ResolutionContext();
                var latestVersion = await NuGetPackageManager.GetLatestVersionAsync(
                    packageIdentity0.Id,
                    msBuildNuGetProject,
                    new ResolutionContext(),
                    sourceRepositoryProvider.GetRepositories().First(),
                    Common.NullLogger.Instance,
                    token);

                var packageLatest = new PackageIdentity(packageIdentity0.Id, latestVersion);

                // Pre-Assert
                // Check that the packages.config file does not exist
                Assert.False(File.Exists(packagesConfigPath));
                // Check that there are no packages returned by PackagesConfigProject
                var packagesInPackagesConfig = (await msBuildNuGetProject.GetInstalledPackagesAsync(token)).ToList();
                Assert.Equal(0, packagesInPackagesConfig.Count);
                Assert.Equal(0, msBuildNuGetProjectSystem.References.Count);

                // Act
                await nuGetPackageManager.InstallPackageAsync(msBuildNuGetProject, packageIdentity0,
                    resolutionContext, new TestNuGetProjectContext(), sourceRepositoryProvider.GetRepositories().First(), null, token);

                // Assert
                // Check that the packages.config file exists after the installation
                Assert.True(File.Exists(packagesConfigPath));
                // Check the number of packages and packages returned by PackagesConfigProject after the installation
                packagesInPackagesConfig = (await msBuildNuGetProject.GetInstalledPackagesAsync(token)).ToList();
                Assert.Equal(1, packagesInPackagesConfig.Count);
                Assert.Equal(packageIdentity0, packagesInPackagesConfig[0].PackageIdentity);
                Assert.Equal(msBuildNuGetProject.MSBuildNuGetProjectSystem.TargetFramework, packagesInPackagesConfig[0].TargetFramework);

                // Main Act
                var packageActions = (await nuGetPackageManager.PreviewUpdatePackagesAsync(
                    msBuildNuGetProject,
                    new ResolutionContext(DependencyBehavior.Highest, false, true, VersionConstraints.None),
                    new TestNuGetProjectContext(),
                    sourceRepositoryProvider.GetRepositories(),
                    sourceRepositoryProvider.GetRepositories(),
                    token)).ToList();

                // Assert
                Assert.Equal(2, packageActions.Count);
                Assert.True(packageIdentity0.Equals(packageActions[0].PackageIdentity));
                Assert.Equal(NuGetProjectActionType.Uninstall, packageActions[0].NuGetProjectActionType);
                Assert.True(packageLatest.Equals(packageActions[1].PackageIdentity));
                Assert.Equal(NuGetProjectActionType.Install, packageActions[1].NuGetProjectActionType);
                Assert.Equal(sourceRepositoryProvider.GetRepositories().Single().PackageSource.Source,
                    packageActions[1].SourceRepository.PackageSource.Source);
            }
        }

        [Fact]
        public async Task TestPacManPreviewUpdatePackageWithTargetPrereleaseInProject()
        {
            // Arrange

            // Set up Package Source
            var packages = new List<SourcePackageDependencyInfo>
            {
                new SourcePackageDependencyInfo("a", new NuGetVersion(1, 0, 0), new Packaging.Core.PackageDependency[] { }, true, null),
                new SourcePackageDependencyInfo("a", new NuGetVersion(2, 0, 0), new Packaging.Core.PackageDependency[] { }, true, null),
                new SourcePackageDependencyInfo("a", new NuGetVersion(3, 0, 0), new Packaging.Core.PackageDependency[] { }, true, null),
                new SourcePackageDependencyInfo("b", new NuGetVersion(1, 0, 0, "beta"), new[] { new Packaging.Core.PackageDependency("a", new VersionRange(new NuGetVersion(1, 0, 0))) }, true, null),
                new SourcePackageDependencyInfo("b", new NuGetVersion(2, 0, 0, "beta"), new[] { new Packaging.Core.PackageDependency("a", new VersionRange(new NuGetVersion(1, 0, 0))) }, true, null),
            };

            var sourceRepositoryProvider = CreateSource(packages);

            // Set up NuGetProject

            var fwk45 = NuGetFramework.Parse("net45");

            var installedPackages = new List<NuGet.Packaging.PackageReference>
            {
                new NuGet.Packaging.PackageReference(new PackageIdentity("a", new NuGetVersion(1, 0, 0)), fwk45, true),
                new NuGet.Packaging.PackageReference(new PackageIdentity("b", new NuGetVersion(1, 0, 0, "beta")), fwk45, true),
            };

            var nuGetProject = new TestNuGetProject(installedPackages);

            // Create Package Manager
            using (var solutionManager = new TestSolutionManager(true))
            {
                var nuGetPackageManager = new NuGetPackageManager(
                    sourceRepositoryProvider,
                    new Configuration.NullSettings(),
                    solutionManager,
                    new TestDeleteOnRestartManager());

                // Main Act
                var result = await nuGetPackageManager.PreviewUpdatePackagesAsync(
                    "a",
                    nuGetProject,
                    new ResolutionContext(),
                    new TestNuGetProjectContext(),
                    sourceRepositoryProvider.GetRepositories(),
                    sourceRepositoryProvider.GetRepositories(),
                    CancellationToken.None);

                // Assert
                var resulting = result.Select(a => Tuple.Create(a.PackageIdentity, a.NuGetProjectActionType)).ToArray();

                var expected = new List<Tuple<PackageIdentity, NuGetProjectActionType>>();
                Expected(expected, "a", new NuGetVersion(1, 0, 0), new NuGetVersion(3, 0, 0));

                Assert.True(Compare(resulting, expected));
            }
        }

        [Fact]
        public async Task TestPacManPreviewUpdatePackageALLPrereleaseInProject()
        {
            // Arrange

            // Set up Package Source
            var packages = new List<SourcePackageDependencyInfo>
            {
                new SourcePackageDependencyInfo("a", new NuGetVersion(1, 0, 0), new Packaging.Core.PackageDependency[] { }, true, null),
                new SourcePackageDependencyInfo("a", new NuGetVersion(2, 0, 0), new Packaging.Core.PackageDependency[] { }, true, null),
                new SourcePackageDependencyInfo("a", new NuGetVersion(3, 0, 0), new Packaging.Core.PackageDependency[] { }, true, null),
                new SourcePackageDependencyInfo("b", new NuGetVersion(1, 0, 0, "beta"), new[] { new Packaging.Core.PackageDependency("a", new VersionRange(new NuGetVersion(1, 0, 0))) }, true, null),
                new SourcePackageDependencyInfo("b", new NuGetVersion(2, 0, 0, "beta"), new[] { new Packaging.Core.PackageDependency("a", new VersionRange(new NuGetVersion(1, 0, 0))) }, true, null),
            };

            var sourceRepositoryProvider = CreateSource(packages);

            // Set up NuGetProject
            var fwk45 = NuGetFramework.Parse("net45");

            var installedPackages = new List<NuGet.Packaging.PackageReference>
            {
                new NuGet.Packaging.PackageReference(new PackageIdentity("a", new NuGetVersion(1, 0, 0)), fwk45, true),
                new NuGet.Packaging.PackageReference(new PackageIdentity("b", new NuGetVersion(1, 0, 0, "beta")), fwk45, true),
            };

            var nuGetProject = new TestNuGetProject(installedPackages);

            // Create Package Manager
            using (var solutionManager = new TestSolutionManager(true))
            {
                var nuGetPackageManager = new NuGetPackageManager(
                    sourceRepositoryProvider,
                    new Configuration.NullSettings(),
                    solutionManager,
                    new TestDeleteOnRestartManager());

                // Main Act
                var result = await nuGetPackageManager.PreviewUpdatePackagesAsync(
                    nuGetProject,
                    new ResolutionContext(),
                    new TestNuGetProjectContext(),
                    sourceRepositoryProvider.GetRepositories(),
                    sourceRepositoryProvider.GetRepositories(),
                    CancellationToken.None);

                // Assert
                var resulting = result.Select(a => Tuple.Create(a.PackageIdentity, a.NuGetProjectActionType)).ToArray();

                var expected = new List<Tuple<PackageIdentity, NuGetProjectActionType>>();
                Expected(expected, "a", new NuGetVersion(1, 0, 0), new NuGetVersion(3, 0, 0));
                Expected(expected, "b", new NuGetVersion(1, 0, 0, "beta"), new NuGetVersion(2, 0, 0, "beta"));

                Assert.True(Compare(resulting, expected));
            }
        }

        [Fact]
        public async Task TestPacManPreviewUpdatePrereleasePackageNoPreFlagSpecified()
        {
            // Arrange

            // Set up Package Source
            var packages = new List<SourcePackageDependencyInfo>
            {
                new SourcePackageDependencyInfo("a", new NuGetVersion(1, 0, 0), new Packaging.Core.PackageDependency[] { }, true, null),
                new SourcePackageDependencyInfo("a", new NuGetVersion(2, 0, 0), new Packaging.Core.PackageDependency[] { }, true, null),
                new SourcePackageDependencyInfo("a", new NuGetVersion(3, 0, 0), new Packaging.Core.PackageDependency[] { }, true, null),
                new SourcePackageDependencyInfo("b", new NuGetVersion(1, 0, 0, "beta"), new[] { new Packaging.Core.PackageDependency("a", new VersionRange(new NuGetVersion(1, 0, 0))) }, true, null),
                new SourcePackageDependencyInfo("b", new NuGetVersion(2, 0, 0, "beta"), new[] { new Packaging.Core.PackageDependency("a", new VersionRange(new NuGetVersion(1, 0, 0))) }, true, null),
            };

            var sourceRepositoryProvider = CreateSource(packages);

            // Set up NuGetProject
            var fwk45 = NuGetFramework.Parse("net45");

            var installedPackages = new List<NuGet.Packaging.PackageReference>
            {
                new NuGet.Packaging.PackageReference(new PackageIdentity("a", new NuGetVersion(1, 0, 0)), fwk45, true),
                new NuGet.Packaging.PackageReference(new PackageIdentity("b", new NuGetVersion(1, 0, 0, "beta")), fwk45, true),
            };

            var nuGetProject = new TestNuGetProject(installedPackages);

            // Create Package Manager
            using (var solutionManager = new TestSolutionManager(true))
            {
                var nuGetPackageManager = new NuGetPackageManager(
                    sourceRepositoryProvider,
                    new Configuration.NullSettings(),
                    solutionManager,
                    new TestDeleteOnRestartManager());

                // Main Act
                var result = await nuGetPackageManager.PreviewUpdatePackagesAsync(
                    "b",
                    nuGetProject,
                    new ResolutionContext(),
                    new TestNuGetProjectContext(),
                    sourceRepositoryProvider.GetRepositories(),
                    sourceRepositoryProvider.GetRepositories(),
                    CancellationToken.None);

                // Assert
                var resulting = result.Select(a => Tuple.Create(a.PackageIdentity, a.NuGetProjectActionType)).ToArray();

                var expected = new List<Tuple<PackageIdentity, NuGetProjectActionType>>();

                Assert.True(Compare(resulting, expected));
            }
        }

        [Fact]
        public async Task TestPacManPreviewUpdateMulti()
        {
            // Arrange

            // Set up Package Source
            var packages = new List<SourcePackageDependencyInfo>
            {
                new SourcePackageDependencyInfo("a", new NuGetVersion(1, 0, 0), new Packaging.Core.PackageDependency[] { }, true, null),
                new SourcePackageDependencyInfo("a", new NuGetVersion(2, 0, 0), new Packaging.Core.PackageDependency[] { }, true, null),
                new SourcePackageDependencyInfo("a", new NuGetVersion(3, 0, 0), new Packaging.Core.PackageDependency[] { }, true, null),
                new SourcePackageDependencyInfo("b", new NuGetVersion(1, 0, 0), new[] { new Packaging.Core.PackageDependency("a", new VersionRange(new NuGetVersion(1, 0, 0))) }, true, null),
                new SourcePackageDependencyInfo("b", new NuGetVersion(2, 0, 0), new[] { new Packaging.Core.PackageDependency("a", new VersionRange(new NuGetVersion(2, 0, 0))) }, true, null),
                new SourcePackageDependencyInfo("b", new NuGetVersion(3, 0, 0), new[] { new Packaging.Core.PackageDependency("a", new VersionRange(new NuGetVersion(2, 0, 0))) }, true, null),
                new SourcePackageDependencyInfo("c", new NuGetVersion(1, 0, 0), new Packaging.Core.PackageDependency[] { }, true, null),
                new SourcePackageDependencyInfo("c", new NuGetVersion(2, 0, 0), new Packaging.Core.PackageDependency[] { }, true, null),
                new SourcePackageDependencyInfo("c", new NuGetVersion(3, 0, 0), new Packaging.Core.PackageDependency[] { }, true, null),
                new SourcePackageDependencyInfo("d", new NuGetVersion(1, 0, 0), new Packaging.Core.PackageDependency[] { }, true, null),
                new SourcePackageDependencyInfo("d", new NuGetVersion(2, 0, 0), new Packaging.Core.PackageDependency[] { }, true, null),
                new SourcePackageDependencyInfo("d", new NuGetVersion(3, 0, 0), new Packaging.Core.PackageDependency[] { }, true, null),
                new SourcePackageDependencyInfo("d", new NuGetVersion(4, 0, 0), new Packaging.Core.PackageDependency[] { }, true, null),
                new SourcePackageDependencyInfo("e", new NuGetVersion(1, 0, 0), new Packaging.Core.PackageDependency[] { }, true, null),
                new SourcePackageDependencyInfo("e", new NuGetVersion(2, 0, 0), new Packaging.Core.PackageDependency[] { }, true, null),
                new SourcePackageDependencyInfo("f", new NuGetVersion(1, 0, 0), new Packaging.Core.PackageDependency[] { }, true, null),
                new SourcePackageDependencyInfo("f", new NuGetVersion(2, 0, 0), new Packaging.Core.PackageDependency[] { }, true, null),
                new SourcePackageDependencyInfo("f", new NuGetVersion(3, 0, 0), new Packaging.Core.PackageDependency[] { }, true, null),
                new SourcePackageDependencyInfo("f", new NuGetVersion(4, 0, 0), new Packaging.Core.PackageDependency[] { }, true, null),
            };

            var sourceRepositoryProvider = CreateSource(packages);

            // Set up NuGetProject
            var fwk45 = NuGetFramework.Parse("net45");

            var installedPackages = new List<NuGet.Packaging.PackageReference>
            {
                new NuGet.Packaging.PackageReference(new PackageIdentity("a", new NuGetVersion(1, 0, 0)), fwk45, true),
                new NuGet.Packaging.PackageReference(new PackageIdentity("b", new NuGetVersion(1, 0, 0)), fwk45, true),
                new NuGet.Packaging.PackageReference(new PackageIdentity("c", new NuGetVersion(2, 0, 0)), fwk45, true),
                new NuGet.Packaging.PackageReference(new PackageIdentity("d", new NuGetVersion(2, 0, 0)), fwk45, true),
                new NuGet.Packaging.PackageReference(new PackageIdentity("e", new NuGetVersion(1, 0, 0)), fwk45, true),
            };

            var nuGetProject = new TestNuGetProject(installedPackages);

            // Create Package Manager
            using (var solutionManager = new TestSolutionManager(true))
            {
                var nuGetPackageManager = new NuGetPackageManager(
                    sourceRepositoryProvider,
                    new Configuration.NullSettings(),
                    solutionManager,
                    new TestDeleteOnRestartManager());

                // Main Act

                var targets = new List<PackageIdentity>
            {
                new PackageIdentity("b", new NuGetVersion(2, 0, 0)),
                new PackageIdentity("c", new NuGetVersion(3, 0, 0)),
                new PackageIdentity("d", new NuGetVersion(3, 0, 0)),
            };

                var result = await nuGetPackageManager.PreviewUpdatePackagesAsync(
                    targets,
                    nuGetProject,
                    new ResolutionContext(),
                    new TestNuGetProjectContext(),
                    sourceRepositoryProvider.GetRepositories(),
                    sourceRepositoryProvider.GetRepositories(),
                    CancellationToken.None);

                // Assert
                var resulting = result.Select(a => Tuple.Create(a.PackageIdentity, a.NuGetProjectActionType)).ToArray();

                var expected = new List<Tuple<PackageIdentity, NuGetProjectActionType>>();
                Expected(expected, "a", new NuGetVersion(1, 0, 0), new NuGetVersion(2, 0, 0));
                Expected(expected, "b", new NuGetVersion(1, 0, 0), new NuGetVersion(2, 0, 0));
                Expected(expected, "c", new NuGetVersion(2, 0, 0), new NuGetVersion(3, 0, 0));
                Expected(expected, "d", new NuGetVersion(2, 0, 0), new NuGetVersion(3, 0, 0));

                Assert.True(Compare(resulting, expected));
            }
        }

        [Fact]
        public async Task TestPacManPreviewInstallPackageFollowingForceUninstall()
        {
            // Arrange

            // Set up Package Source
            var packages = new List<SourcePackageDependencyInfo>
            {
                new SourcePackageDependencyInfo("a", new NuGetVersion(1, 0, 0), new Packaging.Core.PackageDependency[] { }, true, null),
                new SourcePackageDependencyInfo("a", new NuGetVersion(2, 0, 0), new Packaging.Core.PackageDependency[] { }, true, null),
                new SourcePackageDependencyInfo("a", new NuGetVersion(3, 0, 0), new Packaging.Core.PackageDependency[] { }, true, null),
                new SourcePackageDependencyInfo("b", new NuGetVersion(1, 0, 0), new[] { new Packaging.Core.PackageDependency("a", new VersionRange(new NuGetVersion(1, 0, 0))) }, true, null),
                new SourcePackageDependencyInfo("b", new NuGetVersion(2, 0, 0), new[] { new Packaging.Core.PackageDependency("a", new VersionRange(new NuGetVersion(2, 0, 0))) }, true, null),
                new SourcePackageDependencyInfo("b", new NuGetVersion(3, 0, 0), new[] { new Packaging.Core.PackageDependency("a", new VersionRange(new NuGetVersion(2, 0, 0))) }, true, null),
                new SourcePackageDependencyInfo("c", new NuGetVersion(1, 0, 0), new Packaging.Core.PackageDependency[] { }, true, null),
                new SourcePackageDependencyInfo("c", new NuGetVersion(2, 0, 0), new Packaging.Core.PackageDependency[] { }, true, null),
                new SourcePackageDependencyInfo("c", new NuGetVersion(3, 0, 0), new Packaging.Core.PackageDependency[] { }, true, null),
                new SourcePackageDependencyInfo("d", new NuGetVersion(1, 0, 0), new[] { new Packaging.Core.PackageDependency("e", new VersionRange(new NuGetVersion(1, 0, 0))) }, true, null),
                new SourcePackageDependencyInfo("d", new NuGetVersion(2, 0, 0), new[] { new Packaging.Core.PackageDependency("e", new VersionRange(new NuGetVersion(1, 0, 0))) }, true, null),
                new SourcePackageDependencyInfo("d", new NuGetVersion(3, 0, 0), new[] { new Packaging.Core.PackageDependency("e", new VersionRange(new NuGetVersion(1, 0, 0))) }, true, null),
                new SourcePackageDependencyInfo("d", new NuGetVersion(4, 0, 0), new[] { new Packaging.Core.PackageDependency("e", new VersionRange(new NuGetVersion(1, 0, 0))) }, true, null),
                new SourcePackageDependencyInfo("e", new NuGetVersion(1, 0, 0), new Packaging.Core.PackageDependency[] { }, true, null),
                new SourcePackageDependencyInfo("e", new NuGetVersion(2, 0, 0), new Packaging.Core.PackageDependency[] { }, true, null),
                new SourcePackageDependencyInfo("f", new NuGetVersion(1, 0, 0), new Packaging.Core.PackageDependency[] { }, true, null),
                new SourcePackageDependencyInfo("f", new NuGetVersion(2, 0, 0), new Packaging.Core.PackageDependency[] { }, true, null),
                new SourcePackageDependencyInfo("f", new NuGetVersion(3, 0, 0), new Packaging.Core.PackageDependency[] { }, true, null),
                new SourcePackageDependencyInfo("f", new NuGetVersion(4, 0, 0), new Packaging.Core.PackageDependency[] { }, true, null),
            };

            var sourceRepositoryProvider = CreateSource(packages);

            // Set up NuGetProject
            var fwk45 = NuGetFramework.Parse("net45");

            var installedPackages = new List<NuGet.Packaging.PackageReference>
            {
                new NuGet.Packaging.PackageReference(new PackageIdentity("a", new NuGetVersion(1, 0, 0)), fwk45, true),
                new NuGet.Packaging.PackageReference(new PackageIdentity("b", new NuGetVersion(1, 0, 0)), fwk45, true),
                new NuGet.Packaging.PackageReference(new PackageIdentity("c", new NuGetVersion(2, 0, 0)), fwk45, true),
                new NuGet.Packaging.PackageReference(new PackageIdentity("d", new NuGetVersion(2, 0, 0)), fwk45, true),
                // No package "e" even though "d" depends on it (the user must have done an uninstall-package with a -force option)
            };

            var nuGetProject = new TestNuGetProject(installedPackages);

            // Create Package Manager
            using (var solutionManager = new TestSolutionManager(true))
            {
                var nuGetPackageManager = new NuGetPackageManager(
                    sourceRepositoryProvider,
                    new Configuration.NullSettings(),
                    solutionManager,
                    new TestDeleteOnRestartManager());

                // Main Act

                var target = new PackageIdentity("f", new NuGetVersion(3, 0, 0));

                var result = await nuGetPackageManager.PreviewInstallPackageAsync(
                    nuGetProject,
                    target,
                    new ResolutionContext(),
                    new TestNuGetProjectContext(),
                    sourceRepositoryProvider.GetRepositories(),
                    sourceRepositoryProvider.GetRepositories(),
                    CancellationToken.None);

                // Assert
                var resulting = result.Select(a => Tuple.Create(a.PackageIdentity, a.NuGetProjectActionType)).ToArray();

                var expected = new List<Tuple<PackageIdentity, NuGetProjectActionType>>();
                Expected(expected, target.Id, target.Version);

                Assert.True(Compare(resulting, expected));
            }
        }

        [Fact]
        public async Task TestPacManPreviewUpdateMultiWithConflict()
        {
            // Arrange

            // Set up Package Source
            var packages = new List<SourcePackageDependencyInfo>
            {
                new SourcePackageDependencyInfo("a", new NuGetVersion(1, 0, 0), new Packaging.Core.PackageDependency[] { }, true, null),
                new SourcePackageDependencyInfo("a", new NuGetVersion(2, 0, 0), new Packaging.Core.PackageDependency[] { }, true, null),
                new SourcePackageDependencyInfo("a", new NuGetVersion(3, 0, 0), new Packaging.Core.PackageDependency[] { }, true, null),
                new SourcePackageDependencyInfo("b", new NuGetVersion(1, 0, 0), new[] { new Packaging.Core.PackageDependency("a", new VersionRange(new NuGetVersion(1, 0, 0), true, new NuGetVersion(1, 0, 0), true)) }, true, null),
                new SourcePackageDependencyInfo("b", new NuGetVersion(2, 0, 0), new[] { new Packaging.Core.PackageDependency("a", new VersionRange(new NuGetVersion(2, 0, 0), true, new NuGetVersion(2, 0, 0), true)) }, true, null),
                new SourcePackageDependencyInfo("b", new NuGetVersion(3, 0, 0), new[] { new Packaging.Core.PackageDependency("a", new VersionRange(new NuGetVersion(3, 0, 0), true, new NuGetVersion(3, 0, 0), true)) }, true, null),
                new SourcePackageDependencyInfo("c", new NuGetVersion(1, 0, 0), new Packaging.Core.PackageDependency[] { }, true, null),
                new SourcePackageDependencyInfo("c", new NuGetVersion(2, 0, 0), new Packaging.Core.PackageDependency[] { }, true, null),
                new SourcePackageDependencyInfo("c", new NuGetVersion(3, 0, 0), new Packaging.Core.PackageDependency[] { }, true, null),
            };

            var sourceRepositoryProvider = CreateSource(packages);

            // Set up NuGetProject
            var fwk45 = NuGetFramework.Parse("net45");

            var installedPackages = new List<NuGet.Packaging.PackageReference>
            {
                new NuGet.Packaging.PackageReference(new PackageIdentity("a", new NuGetVersion(1, 0, 0)), fwk45, true),
                new NuGet.Packaging.PackageReference(new PackageIdentity("b", new NuGetVersion(1, 0, 0)), fwk45, true),
                new NuGet.Packaging.PackageReference(new PackageIdentity("c", new NuGetVersion(2, 0, 0)), fwk45, true),
            };

            var nuGetProject = new TestNuGetProject(installedPackages);

            // Create Package Manager
            using (var solutionManager = new TestSolutionManager(true))
            {
                var nuGetPackageManager = new NuGetPackageManager(
                    sourceRepositoryProvider,
                    new Configuration.NullSettings(),
                    solutionManager,
                    new TestDeleteOnRestartManager());

                // Main Act

                var targets = new List<PackageIdentity>
                {
                    new PackageIdentity("a", new NuGetVersion(2, 0, 0)),
                    new PackageIdentity("b", new NuGetVersion(3, 0, 0)),
                };

                try
                {
                    await nuGetPackageManager.PreviewUpdatePackagesAsync(
                        targets,
                        nuGetProject,
                        new ResolutionContext(),
                        new TestNuGetProjectContext(),
                        sourceRepositoryProvider.GetRepositories(),
                        sourceRepositoryProvider.GetRepositories(),
                        CancellationToken.None);

                    Assert.True(false);
                }
                catch (Exception e)
                {
                    Assert.IsType(typeof(InvalidOperationException), e);
                }
            }
        }

        // [Fact] -- This test performs update but verifies for a specific version
        //           This is not going to work as newer versions are uploaded
        public async Task TestPacManPreviewUpdatePackages()
        {
            // Arrange
            var sourceRepositoryProvider = TestSourceRepositoryUtility.CreateV2OnlySourceRepositoryProvider();
            using (var testSolutionManager = new TestSolutionManager(true))
            {
                var testSettings = new Configuration.NullSettings();
                var token = CancellationToken.None;
                var deleteOnRestartManager = new TestDeleteOnRestartManager();
                var nuGetPackageManager = new NuGetPackageManager(
                    sourceRepositoryProvider,
                    testSettings,
                    testSolutionManager,
                    deleteOnRestartManager);
                var packagesFolderPath = PackagesFolderPathUtility.GetPackagesFolderPath(testSolutionManager, testSettings);

                var msBuildNuGetProject = testSolutionManager.AddNewMSBuildProject();
                var msBuildNuGetProjectSystem = msBuildNuGetProject.MSBuildNuGetProjectSystem as TestMSBuildNuGetProjectSystem;
                var packagesConfigPath = msBuildNuGetProject.PackagesConfigNuGetProject.FullPath;
                var packageIdentity = MorePackageWithDependents[3]; // Microsoft.Net.Http.2.2.22

                // Pre-Assert
                // Check that the packages.config file does not exist
                Assert.False(File.Exists(packagesConfigPath));
                // Check that there are no packages returned by PackagesConfigProject
                var packagesInPackagesConfig = (await msBuildNuGetProject.PackagesConfigNuGetProject.GetInstalledPackagesAsync(token)).ToList();
                Assert.Equal(0, packagesInPackagesConfig.Count);
                Assert.Equal(0, msBuildNuGetProjectSystem.References.Count);

                // Act
                await nuGetPackageManager.InstallPackageAsync(msBuildNuGetProject, packageIdentity,
                    new ResolutionContext(), new TestNuGetProjectContext(), sourceRepositoryProvider.GetRepositories().First(), null, token);

                // Assert
                // Check that the packages.config file exists after the installation
                Assert.True(File.Exists(packagesConfigPath));
                // Check the number of packages and packages returned by PackagesConfigProject after the installation
                packagesInPackagesConfig = (await msBuildNuGetProject.PackagesConfigNuGetProject.GetInstalledPackagesAsync(token)).ToList();
                Assert.Equal(3, packagesInPackagesConfig.Count);
                Assert.Equal(packageIdentity, packagesInPackagesConfig[2].PackageIdentity);
                Assert.Equal(msBuildNuGetProject.MSBuildNuGetProjectSystem.TargetFramework, packagesInPackagesConfig[2].TargetFramework);
                Assert.Equal(MorePackageWithDependents[0], packagesInPackagesConfig[1].PackageIdentity);
                Assert.Equal(msBuildNuGetProject.MSBuildNuGetProjectSystem.TargetFramework, packagesInPackagesConfig[1].TargetFramework);
                Assert.Equal(MorePackageWithDependents[2], packagesInPackagesConfig[0].PackageIdentity);
                Assert.Equal(msBuildNuGetProject.MSBuildNuGetProjectSystem.TargetFramework, packagesInPackagesConfig[0].TargetFramework);

                // Main Act
                var packageActions = (await nuGetPackageManager.PreviewUpdatePackagesAsync(
                    msBuildNuGetProject,
                    new ResolutionContext(DependencyBehavior.Highest, false, true, VersionConstraints.None),
                    new TestNuGetProjectContext(),
                    sourceRepositoryProvider.GetRepositories(),
                    sourceRepositoryProvider.GetRepositories(),
                    token)).ToList();

                // Assert
                Assert.Equal(4, packageActions.Count);
                Assert.True(MorePackageWithDependents[0].Equals(packageActions[0].PackageIdentity));
                Assert.Equal(NuGetProjectActionType.Uninstall, packageActions[0].NuGetProjectActionType);
                Assert.True(MorePackageWithDependents[3].Equals(packageActions[1].PackageIdentity));
                Assert.Equal(NuGetProjectActionType.Uninstall, packageActions[1].NuGetProjectActionType);
                Assert.True(MorePackageWithDependents[1].Equals(packageActions[2].PackageIdentity));
                Assert.Equal(NuGetProjectActionType.Install, packageActions[2].NuGetProjectActionType);
                Assert.Equal(sourceRepositoryProvider.GetRepositories().Single().PackageSource.Source,
                    packageActions[2].SourceRepository.PackageSource.Source);
                Assert.True(MorePackageWithDependents[4].Equals(packageActions[3].PackageIdentity));
                Assert.Equal(NuGetProjectActionType.Install, packageActions[3].NuGetProjectActionType);
                Assert.Equal(sourceRepositoryProvider.GetRepositories().Single().PackageSource.Source,
                    packageActions[3].SourceRepository.PackageSource.Source);
            }
        }

        [Fact]
        public async Task TestPacManPreviewReinstallPackages()
        {
            // Arrange
            var sourceRepositoryProvider = TestSourceRepositoryUtility.CreateV2OnlySourceRepositoryProvider();
            using (var testSolutionManager = new TestSolutionManager(true))
            {
                var testSettings = new Configuration.NullSettings();
                var token = CancellationToken.None;
                var deleteOnRestartManager = new TestDeleteOnRestartManager();
                var nuGetPackageManager = new NuGetPackageManager(
                    sourceRepositoryProvider,
                    testSettings,
                    testSolutionManager,
                    deleteOnRestartManager);
                var packagesFolderPath = PackagesFolderPathUtility.GetPackagesFolderPath(testSolutionManager, testSettings);

                var msBuildNuGetProject = testSolutionManager.AddNewMSBuildProject();
                var msBuildNuGetProjectSystem = msBuildNuGetProject.MSBuildNuGetProjectSystem as TestMSBuildNuGetProjectSystem;
                var packagesConfigPath = msBuildNuGetProject.PackagesConfigNuGetProject.FullPath;
                var packageIdentity = MorePackageWithDependents[3]; // Microsoft.Net.Http.2.2.22

                // Pre-Assert
                // Check that the packages.config file does not exist
                Assert.False(File.Exists(packagesConfigPath));
                // Check that there are no packages returned by PackagesConfigProject
                var packagesInPackagesConfig = (await msBuildNuGetProject.PackagesConfigNuGetProject.GetInstalledPackagesAsync(token)).ToList();
                Assert.Equal(0, packagesInPackagesConfig.Count);
                Assert.Equal(0, msBuildNuGetProjectSystem.References.Count);

                // Act
                await nuGetPackageManager.InstallPackageAsync(msBuildNuGetProject, packageIdentity,
                    new ResolutionContext(), new TestNuGetProjectContext(), sourceRepositoryProvider.GetRepositories().First(), null, token);

                // Assert
                // Check that the packages.config file exists after the installation
                Assert.True(File.Exists(packagesConfigPath));
                // Check the number of packages and packages returned by PackagesConfigProject after the installation
                packagesInPackagesConfig = (await msBuildNuGetProject.PackagesConfigNuGetProject.GetInstalledPackagesAsync(token)).ToList();
                Assert.Equal(3, packagesInPackagesConfig.Count);
                Assert.Equal(packageIdentity, packagesInPackagesConfig[2].PackageIdentity);
                Assert.Equal(msBuildNuGetProject.MSBuildNuGetProjectSystem.TargetFramework, packagesInPackagesConfig[2].TargetFramework);
                Assert.Equal(MorePackageWithDependents[0], packagesInPackagesConfig[1].PackageIdentity);
                Assert.Equal(msBuildNuGetProject.MSBuildNuGetProjectSystem.TargetFramework, packagesInPackagesConfig[1].TargetFramework);
                Assert.Equal(MorePackageWithDependents[2], packagesInPackagesConfig[0].PackageIdentity);
                Assert.Equal(msBuildNuGetProject.MSBuildNuGetProjectSystem.TargetFramework, packagesInPackagesConfig[0].TargetFramework);

                var resolutionContext = new ResolutionContext(
                    DependencyBehavior.Highest,
                    false,
                    true,
                    VersionConstraints.ExactMajor | VersionConstraints.ExactMinor | VersionConstraints.ExactPatch | VersionConstraints.ExactRelease);

                // Main Act
                var packageActions = (await nuGetPackageManager.PreviewUpdatePackagesAsync(
                    msBuildNuGetProject,
                    resolutionContext,
                    new TestNuGetProjectContext(),
                    sourceRepositoryProvider.GetRepositories(),
                    sourceRepositoryProvider.GetRepositories(),
                    token)).ToList();

                // Assert
                var singlePackageSource = sourceRepositoryProvider.GetRepositories().Single().PackageSource.Source;
                Assert.Equal(6, packageActions.Count);

                Assert.True(MorePackageWithDependents[3].Equals(packageActions[0].PackageIdentity));
                Assert.Equal(NuGetProjectActionType.Uninstall, packageActions[0].NuGetProjectActionType);
                Assert.True(MorePackageWithDependents[2].Equals(packageActions[1].PackageIdentity));
                Assert.Equal(NuGetProjectActionType.Uninstall, packageActions[1].NuGetProjectActionType);
                Assert.True(MorePackageWithDependents[0].Equals(packageActions[2].PackageIdentity));
                Assert.Equal(NuGetProjectActionType.Uninstall, packageActions[2].NuGetProjectActionType);

                Assert.True(MorePackageWithDependents[0].Equals(packageActions[3].PackageIdentity));
                Assert.Equal(NuGetProjectActionType.Install, packageActions[3].NuGetProjectActionType);
                Assert.Equal(singlePackageSource, packageActions[3].SourceRepository.PackageSource.Source);
                Assert.True(MorePackageWithDependents[2].Equals(packageActions[4].PackageIdentity));
                Assert.Equal(NuGetProjectActionType.Install, packageActions[4].NuGetProjectActionType);
                Assert.Equal(singlePackageSource, packageActions[4].SourceRepository.PackageSource.Source);
                Assert.True(MorePackageWithDependents[3].Equals(packageActions[5].PackageIdentity));
                Assert.Equal(NuGetProjectActionType.Install, packageActions[5].NuGetProjectActionType);
                Assert.Equal(singlePackageSource, packageActions[5].SourceRepository.PackageSource.Source);
            }
        }

        [Fact]
        public async Task TestPacManReinstallPackages()
        {
            // Arrange
            var sourceRepositoryProvider = TestSourceRepositoryUtility.CreateV2OnlySourceRepositoryProvider();
            using (var testSolutionManager = new TestSolutionManager(true))
            {
                var testSettings = new Configuration.NullSettings();
                var token = CancellationToken.None;
                var deleteOnRestartManager = new TestDeleteOnRestartManager();
                var nuGetPackageManager = new NuGetPackageManager(
                    sourceRepositoryProvider,
                    testSettings,
                    testSolutionManager,
                    deleteOnRestartManager);
                var packagesFolderPath = PackagesFolderPathUtility.GetPackagesFolderPath(testSolutionManager, testSettings);
                var packagePathResolver = new PackagePathResolver(packagesFolderPath);

                var msBuildNuGetProject = testSolutionManager.AddNewMSBuildProject();
                var msBuildNuGetProjectSystem = msBuildNuGetProject.MSBuildNuGetProjectSystem as TestMSBuildNuGetProjectSystem;
                var packagesConfigPath = msBuildNuGetProject.PackagesConfigNuGetProject.FullPath;
                var folderNuGetProject = msBuildNuGetProject.FolderNuGetProject;
                var packageIdentity = MorePackageWithDependents[3]; // Microsoft.Net.Http.2.2.22

                // Pre-Assert
                // Check that the packages.config file does not exist
                Assert.False(File.Exists(packagesConfigPath));
                // Check that there are no packages returned by PackagesConfigProject
                var packagesInPackagesConfig = (await msBuildNuGetProject.PackagesConfigNuGetProject.GetInstalledPackagesAsync(token)).ToList();
                Assert.Equal(0, packagesInPackagesConfig.Count);
                Assert.Equal(0, msBuildNuGetProjectSystem.References.Count);

                // Act
                await nuGetPackageManager.InstallPackageAsync(msBuildNuGetProject, packageIdentity,
                    new ResolutionContext(), new TestNuGetProjectContext(), sourceRepositoryProvider.GetRepositories().First(), null, token);

                // Assert
                // Check that the packages.config file exists after the installation
                Assert.True(File.Exists(packagesConfigPath));
                // Check the number of packages and packages returned by PackagesConfigProject after the installation
                packagesInPackagesConfig = (await msBuildNuGetProject.PackagesConfigNuGetProject.GetInstalledPackagesAsync(token)).ToList();
                Assert.Equal(3, packagesInPackagesConfig.Count);
                Assert.Equal(packageIdentity, packagesInPackagesConfig[2].PackageIdentity);
                Assert.Equal(msBuildNuGetProject.MSBuildNuGetProjectSystem.TargetFramework, packagesInPackagesConfig[2].TargetFramework);
                Assert.Equal(MorePackageWithDependents[0], packagesInPackagesConfig[1].PackageIdentity);
                Assert.Equal(msBuildNuGetProject.MSBuildNuGetProjectSystem.TargetFramework, packagesInPackagesConfig[1].TargetFramework);
                Assert.Equal(MorePackageWithDependents[2], packagesInPackagesConfig[0].PackageIdentity);
                Assert.Equal(msBuildNuGetProject.MSBuildNuGetProjectSystem.TargetFramework, packagesInPackagesConfig[0].TargetFramework);
                var installedPackageIdentities = (await msBuildNuGetProject.GetInstalledPackagesAsync(token))
                    .Select(pr => pr.PackageIdentity);

                var resolutionContext = new ResolutionContext(
                    DependencyBehavior.Highest,
                    false,
                    true,
                    VersionConstraints.ExactMajor | VersionConstraints.ExactMinor | VersionConstraints.ExactPatch | VersionConstraints.ExactRelease);

                // Act
                var packageActions = (await nuGetPackageManager.PreviewUpdatePackagesAsync(
                    msBuildNuGetProject,
                    resolutionContext,
                    new TestNuGetProjectContext(),
                    sourceRepositoryProvider.GetRepositories(),
                    sourceRepositoryProvider.GetRepositories(),
                    token)).ToList();

                // Assert
                var singlePackageSource = sourceRepositoryProvider.GetRepositories().Single().PackageSource.Source;
                Assert.Equal(6, packageActions.Count);
                Assert.True(MorePackageWithDependents[3].Equals(packageActions[0].PackageIdentity));
                Assert.Equal(NuGetProjectActionType.Uninstall, packageActions[0].NuGetProjectActionType);
                Assert.True(MorePackageWithDependents[2].Equals(packageActions[1].PackageIdentity));
                Assert.Equal(NuGetProjectActionType.Uninstall, packageActions[1].NuGetProjectActionType);
                Assert.True(MorePackageWithDependents[0].Equals(packageActions[2].PackageIdentity));
                Assert.Equal(NuGetProjectActionType.Uninstall, packageActions[2].NuGetProjectActionType);

                Assert.True(MorePackageWithDependents[0].Equals(packageActions[3].PackageIdentity));
                Assert.Equal(NuGetProjectActionType.Install, packageActions[3].NuGetProjectActionType);
                Assert.Equal(singlePackageSource, packageActions[3].SourceRepository.PackageSource.Source);
                Assert.True(MorePackageWithDependents[2].Equals(packageActions[4].PackageIdentity));
                Assert.Equal(NuGetProjectActionType.Install, packageActions[4].NuGetProjectActionType);
                Assert.Equal(singlePackageSource, packageActions[4].SourceRepository.PackageSource.Source);
                Assert.True(MorePackageWithDependents[3].Equals(packageActions[5].PackageIdentity));
                Assert.Equal(NuGetProjectActionType.Install, packageActions[5].NuGetProjectActionType);
                Assert.Equal(singlePackageSource, packageActions[5].SourceRepository.PackageSource.Source);

                // Main Act
                await nuGetPackageManager.ExecuteNuGetProjectActionsAsync(msBuildNuGetProject, packageActions, new TestNuGetProjectContext(), token);

                // Check that the packages.config file exists after the installation
                Assert.True(File.Exists(packagesConfigPath));
                // Check the number of packages and packages returned by PackagesConfigProject after the installation
                packagesInPackagesConfig = (await msBuildNuGetProject.PackagesConfigNuGetProject.GetInstalledPackagesAsync(token)).ToList();
                Assert.Equal(3, packagesInPackagesConfig.Count);
                Assert.Equal(packageIdentity, packagesInPackagesConfig[2].PackageIdentity);
                Assert.Equal(msBuildNuGetProject.MSBuildNuGetProjectSystem.TargetFramework, packagesInPackagesConfig[2].TargetFramework);
                Assert.Equal(MorePackageWithDependents[0], packagesInPackagesConfig[1].PackageIdentity);
                Assert.Equal(msBuildNuGetProject.MSBuildNuGetProjectSystem.TargetFramework, packagesInPackagesConfig[1].TargetFramework);
                Assert.Equal(MorePackageWithDependents[2], packagesInPackagesConfig[0].PackageIdentity);
                Assert.Equal(msBuildNuGetProject.MSBuildNuGetProjectSystem.TargetFramework, packagesInPackagesConfig[0].TargetFramework);
                Assert.True(File.Exists(folderNuGetProject.GetInstalledPackageFilePath(packageIdentity)));
                Assert.True(File.Exists(folderNuGetProject.GetInstalledPackageFilePath(MorePackageWithDependents[0])));
                Assert.True(File.Exists(folderNuGetProject.GetInstalledPackageFilePath(MorePackageWithDependents[2])));
            }
        }

        [Fact]
        public async Task TestPacManReinstallSpecificPackage()
        {
            // Arrange

            // Set up Package Source
            var packages = new List<SourcePackageDependencyInfo>
            {
                new SourcePackageDependencyInfo("a", new NuGetVersion(1, 0, 0), new Packaging.Core.PackageDependency[] { }, true, null),
                new SourcePackageDependencyInfo("a", new NuGetVersion(2, 0, 0), new Packaging.Core.PackageDependency[] { }, true, null),
                new SourcePackageDependencyInfo("a", new NuGetVersion(3, 0, 0), new Packaging.Core.PackageDependency[] { }, true, null),
                new SourcePackageDependencyInfo("b", new NuGetVersion(1, 0, 0), new[]
                {
                    new Packaging.Core.PackageDependency("a", new VersionRange(new NuGetVersion(1, 0, 0))),
                    new Packaging.Core.PackageDependency("c", new VersionRange(new NuGetVersion(2, 0, 0))),
                }, true, null),
                new SourcePackageDependencyInfo("b", new NuGetVersion(2, 0, 0), new[]
                {
                    new Packaging.Core.PackageDependency("a", new VersionRange(new NuGetVersion(2, 0, 0))),
                    new Packaging.Core.PackageDependency("c", new VersionRange(new NuGetVersion(2, 0, 0))),
                }, true, null),
                new SourcePackageDependencyInfo("b", new NuGetVersion(3, 0, 0), new[]
                {
                    new Packaging.Core.PackageDependency("a", new VersionRange(new NuGetVersion(3, 0, 0))),
                }, true, null),
                new SourcePackageDependencyInfo("c", new NuGetVersion(1, 0, 0), new[]
                {
                    new Packaging.Core.PackageDependency("d", new VersionRange(new NuGetVersion(2, 0, 0))),
                }, true, null),
                new SourcePackageDependencyInfo("c", new NuGetVersion(2, 0, 0), new[]
                {
                    new Packaging.Core.PackageDependency("d", new VersionRange(new NuGetVersion(2, 0, 0))),
                }, true, null),
                new SourcePackageDependencyInfo("c", new NuGetVersion(3, 0, 0), new Packaging.Core.PackageDependency[] { }, true, null),
                new SourcePackageDependencyInfo("d", new NuGetVersion(1, 0, 0), new Packaging.Core.PackageDependency[] { }, true, null),
                new SourcePackageDependencyInfo("d", new NuGetVersion(2, 0, 0), new Packaging.Core.PackageDependency[] { }, true, null),
                new SourcePackageDependencyInfo("d", new NuGetVersion(3, 0, 0), new Packaging.Core.PackageDependency[] { }, true, null),
                new SourcePackageDependencyInfo("d", new NuGetVersion(4, 0, 0), new Packaging.Core.PackageDependency[] { }, true, null),
                new SourcePackageDependencyInfo("e", new NuGetVersion(1, 0, 0), new Packaging.Core.PackageDependency[] { }, true, null),
                new SourcePackageDependencyInfo("e", new NuGetVersion(2, 0, 0), new Packaging.Core.PackageDependency[] { }, true, null),
                new SourcePackageDependencyInfo("f", new NuGetVersion(1, 0, 0), new Packaging.Core.PackageDependency[] { }, true, null),
                new SourcePackageDependencyInfo("f", new NuGetVersion(2, 0, 0), new Packaging.Core.PackageDependency[] { }, true, null),
                new SourcePackageDependencyInfo("f", new NuGetVersion(3, 0, 0), new Packaging.Core.PackageDependency[] { }, true, null),
                new SourcePackageDependencyInfo("f", new NuGetVersion(4, 0, 0), new Packaging.Core.PackageDependency[] { }, true, null),
            };

            var sourceRepositoryProvider = CreateSource(packages);

            // Set up NuGetProject
            var fwk45 = NuGetFramework.Parse("net45");

            var installedPackages = new List<NuGet.Packaging.PackageReference>
            {
                new NuGet.Packaging.PackageReference(new PackageIdentity("a", new NuGetVersion(1, 0, 0)), fwk45, true),
                new NuGet.Packaging.PackageReference(new PackageIdentity("b", new NuGetVersion(1, 0, 0)), fwk45, true),
                new NuGet.Packaging.PackageReference(new PackageIdentity("c", new NuGetVersion(2, 0, 0)), fwk45, true),
                new NuGet.Packaging.PackageReference(new PackageIdentity("d", new NuGetVersion(2, 0, 0)), fwk45, true),
                new NuGet.Packaging.PackageReference(new PackageIdentity("e", new NuGetVersion(1, 0, 0)), fwk45, true),
                new NuGet.Packaging.PackageReference(new PackageIdentity("f", new NuGetVersion(3, 0, 0)), fwk45, true),
            };

            var nuGetProject = new TestNuGetProject(installedPackages);

            // Create Package Manager

            using (var solutionManager = new TestSolutionManager(true))
            {
                var nuGetPackageManager = new NuGetPackageManager(
                    sourceRepositoryProvider,
                    new Configuration.NullSettings(),
                    solutionManager,
                    new TestDeleteOnRestartManager());

                // Main Act

                var resolutionContext = new ResolutionContext(
                    DependencyBehavior.Highest,
                    false,
                    true,
                    VersionConstraints.ExactMajor | VersionConstraints.ExactMinor | VersionConstraints.ExactPatch | VersionConstraints.ExactRelease);

                var result = await nuGetPackageManager.PreviewUpdatePackagesAsync(
                    "b",
                    nuGetProject,
                    resolutionContext,
                    new TestNuGetProjectContext(),
                    sourceRepositoryProvider.GetRepositories(),
                    sourceRepositoryProvider.GetRepositories(),
                    CancellationToken.None);

                // Assert
                var resulting = result.Select(a => Tuple.Create(a.PackageIdentity, a.NuGetProjectActionType)).ToArray();

                var expected = new List<Tuple<PackageIdentity, NuGetProjectActionType>>();
                Expected(expected, "a", new NuGetVersion(1, 0, 0), new NuGetVersion(1, 0, 0));
                Expected(expected, "b", new NuGetVersion(1, 0, 0), new NuGetVersion(1, 0, 0));
                Expected(expected, "c", new NuGetVersion(2, 0, 0), new NuGetVersion(2, 0, 0));
                Expected(expected, "d", new NuGetVersion(2, 0, 0), new NuGetVersion(2, 0, 0));
                // note e and f are not touched

                Assert.True(Compare(resulting, expected));
            }
        }

        [Fact]
        public async Task TestPacManOpenReadmeFile()
        {
            // Arrange
            var sourceRepositoryProvider = TestSourceRepositoryUtility.CreateV3OnlySourceRepositoryProvider();
            using (var testSolutionManager = new TestSolutionManager(true))
            {
                var testSettings = new Configuration.NullSettings();
                var token = CancellationToken.None;
                var deleteOnRestartManager = new TestDeleteOnRestartManager();
                var nuGetPackageManager = new NuGetPackageManager(
                    sourceRepositoryProvider,
                    testSettings,
                    testSolutionManager,
                    deleteOnRestartManager);
                var packagesFolderPath = PackagesFolderPathUtility.GetPackagesFolderPath(testSolutionManager, testSettings);
                var packagePathResolver = new PackagePathResolver(packagesFolderPath);

                var msBuildNuGetProject = testSolutionManager.AddNewMSBuildProject();
                var msBuildNuGetProjectSystem = msBuildNuGetProject.MSBuildNuGetProjectSystem as TestMSBuildNuGetProjectSystem;
                var packagesConfigPath = msBuildNuGetProject.PackagesConfigNuGetProject.FullPath;
                var packageIdentity = new PackageIdentity("elmah", new NuGetVersion("1.2.2"));

                // Pre-Assert
                // Check that the packages.config file does not exist
                Assert.False(File.Exists(packagesConfigPath));
                // Check that there are no packages returned by PackagesConfigProject
                var packagesInPackagesConfig = (await msBuildNuGetProject.PackagesConfigNuGetProject.GetInstalledPackagesAsync(token)).ToList();
                Assert.Equal(0, packagesInPackagesConfig.Count);
                Assert.Equal(0, msBuildNuGetProjectSystem.References.Count);

                // Act
                // Set the direct install on the execution context of INuGetProjectContext before installing a package
                var testNuGetProjectContext = new TestNuGetProjectContext();
                testNuGetProjectContext.TestExecutionContext = new TestExecutionContext(packageIdentity);
                await nuGetPackageManager.InstallPackageAsync(msBuildNuGetProject, packageIdentity,
                    new ResolutionContext(), testNuGetProjectContext, sourceRepositoryProvider.GetRepositories().First(), null, token);

                // Assert
                // Check that the packages.config file exists after the installation
                Assert.True(File.Exists(packagesConfigPath));
                // Check the number of packages and packages returned by PackagesConfigProject after the installation
                packagesInPackagesConfig = (await msBuildNuGetProject.PackagesConfigNuGetProject.GetInstalledPackagesAsync(token)).ToList();
                Assert.Equal(2, packagesInPackagesConfig.Count);
                Assert.Equal(packageIdentity, packagesInPackagesConfig[0].PackageIdentity);
                Assert.Equal(msBuildNuGetProject.MSBuildNuGetProjectSystem.TargetFramework, packagesInPackagesConfig[0].TargetFramework);
                Assert.Equal(1, testNuGetProjectContext.TestExecutionContext.FilesOpened.Count);
                Assert.True(string.Equals(Path.Combine(packagePathResolver.GetInstallPath(packageIdentity), "ReadMe.txt"),
                    testNuGetProjectContext.TestExecutionContext.FilesOpened.First(), StringComparison.OrdinalIgnoreCase));
            }
        }

        [Fact]
        public async Task TestPacManPreviewInstallPackageIdUnexpectedDowngrade()
        {
            // Arrange
            var sourceRepositoryProvider = TestSourceRepositoryUtility.CreateV2OnlySourceRepositoryProvider();
            using (var testSolutionManager = new TestSolutionManager(true))
            {
                var testSettings = new Configuration.NullSettings();
                var token = CancellationToken.None;
                var deleteOnRestartManager = new TestDeleteOnRestartManager();
                var nuGetPackageManager = new NuGetPackageManager(
                    sourceRepositoryProvider,
                    testSettings,
                    testSolutionManager,
                    deleteOnRestartManager);
                var packagesFolderPath = PackagesFolderPathUtility.GetPackagesFolderPath(testSolutionManager, testSettings);

                var msBuildNuGetProject = testSolutionManager.AddNewMSBuildProject("TestProjectName");
                var msBuildNuGetProjectSystem = msBuildNuGetProject.MSBuildNuGetProjectSystem as TestMSBuildNuGetProjectSystem;
                var packagesConfigPath = msBuildNuGetProject.PackagesConfigNuGetProject.FullPath;
                var packageId = "Newtonsoft.Json";
                var testNuGetProjectContext = new TestNuGetProjectContext();
                var primarySourceRepository = sourceRepositoryProvider.GetRepositories().First();

                // Pre-Assert
                // Check that the packages.config file does not exist
                Assert.False(File.Exists(packagesConfigPath));
                // Check that there are no packages returned by PackagesConfigProject
                var packagesInPackagesConfig = (await msBuildNuGetProject.PackagesConfigNuGetProject.GetInstalledPackagesAsync(token)).ToList();
                Assert.Equal(0, packagesInPackagesConfig.Count);
                Assert.Equal(0, msBuildNuGetProjectSystem.References.Count);

                // Act
                await nuGetPackageManager.InstallPackageAsync(msBuildNuGetProject, packageId, new ResolutionContext(DependencyBehavior.Lowest, includePrelease: false, includeUnlisted: false, versionConstraints: VersionConstraints.None),
                    testNuGetProjectContext, primarySourceRepository, null, token);

                // Check that the packages.config file does not exist
                Assert.True(File.Exists(packagesConfigPath));

                // Check that there are no packages returned by PackagesConfigProject
                packagesInPackagesConfig = (await msBuildNuGetProject.PackagesConfigNuGetProject.GetInstalledPackagesAsync(token)).ToList();
                Assert.Equal(1, packagesInPackagesConfig.Count);
                Assert.Equal(1, msBuildNuGetProjectSystem.References.Count);

                Exception exception = null;
                try
                {
                    var packageActions = (await nuGetPackageManager.PreviewInstallPackageAsync(msBuildNuGetProject, packageId,
                        new ResolutionContext(), testNuGetProjectContext, primarySourceRepository, null, token)).ToList();
                }
                catch (Exception ex)
                {
                    exception = ex;
                }

                Assert.NotNull(exception);
                Assert.True(exception is InvalidOperationException);
                Assert.Contains("Package 'Newtonsoft.Json.", exception.Message);
                Assert.Contains("already exists in project 'TestProjectName'", exception.Message);
            }
        }

        [Fact]
        public async Task TestPacManPreviewInstallPackageThrowsDependencyDowngrade()
        {
            // Arrange
            var packageIdentityA = new PackageIdentity("DotNetOpenAuth.OAuth.Core", new NuGetVersion("4.3.2.13293"));
            var packageIdentityB1 = new PackageIdentity("DotNetOpenAuth.Core", new NuGetVersion("4.3.2.13293"));
            var packageIdentityB2 = new PackageIdentity("DotNetOpenAuth.Core", new NuGetVersion("4.3.4.13329"));
            var sourceRepositoryProvider = TestSourceRepositoryUtility.CreateV2OnlySourceRepositoryProvider();
            using (var testSolutionManager = new TestSolutionManager(true))
            {
                var testSettings = new Configuration.NullSettings();
                var token = CancellationToken.None;
                var deleteOnRestartManager = new TestDeleteOnRestartManager();
                var nuGetPackageManager = new NuGetPackageManager(
                    sourceRepositoryProvider,
                    testSettings,
                    testSolutionManager,
                    deleteOnRestartManager);
                var packagesFolderPath = PackagesFolderPathUtility.GetPackagesFolderPath(testSolutionManager, testSettings);

                var msBuildNuGetProject = testSolutionManager.AddNewMSBuildProject();
                var msBuildNuGetProjectSystem = msBuildNuGetProject.MSBuildNuGetProjectSystem as TestMSBuildNuGetProjectSystem;
                var packagesConfigPath = msBuildNuGetProject.PackagesConfigNuGetProject.FullPath;
                var testNuGetProjectContext = new TestNuGetProjectContext();
                var primarySourceRepository = sourceRepositoryProvider.GetRepositories().First();

                // Pre-Assert
                // Check that the packages.config file does not exist
                Assert.False(File.Exists(packagesConfigPath));
                // Check that there are no packages returned by PackagesConfigProject
                var packagesInPackagesConfig = (await msBuildNuGetProject.PackagesConfigNuGetProject.GetInstalledPackagesAsync(token)).ToList();
                Assert.Equal(0, packagesInPackagesConfig.Count);
                Assert.Equal(0, msBuildNuGetProjectSystem.References.Count);

                // Act
                await nuGetPackageManager.InstallPackageAsync(msBuildNuGetProject, packageIdentityB2, new ResolutionContext(DependencyBehavior.Lowest, includePrelease: true, includeUnlisted: false, versionConstraints: VersionConstraints.None),
                    testNuGetProjectContext, primarySourceRepository, null, token);

                // Check that the packages.config file does not exist
                Assert.True(File.Exists(packagesConfigPath));
                // Check that there are no packages returned by PackagesConfigProject
                packagesInPackagesConfig = (await msBuildNuGetProject.PackagesConfigNuGetProject.GetInstalledPackagesAsync(token)).ToList();
                Assert.Equal(2, packagesInPackagesConfig.Count);

                Exception exception = null;
                try
                {
                    var packageActions = (await nuGetPackageManager.PreviewInstallPackageAsync(msBuildNuGetProject, packageIdentityA,
                        new ResolutionContext(), testNuGetProjectContext, primarySourceRepository, null, token)).ToList();
                }
                catch (Exception ex)
                {
                    exception = ex;
                }

                Assert.NotNull(exception);
                Assert.True(exception is InvalidOperationException);
                Assert.Equal(
                    string.Format("Unable to resolve dependencies. '{0} {1}' is not compatible with '{2} {3} constraint: {4} (= {5})'.",
                        packageIdentityB2.Id,
                        packageIdentityB2.Version,
                        packageIdentityA.Id,
                        packageIdentityA.Version,
                        packageIdentityB1.Id,
                        packageIdentityB1.Version),
                    exception.Message);
            }
        }

        [Fact]
        public async Task TestPacManPreviewInstallDependencyVersionHighestAndPrerelease()
        {
            // Arrange
            var sourceRepositoryProvider = TestSourceRepositoryUtility.CreateV3OnlySourceRepositoryProvider();
            using (var testSolutionManager = new TestSolutionManager(true))
            {
                var testSettings = new Configuration.NullSettings();
                var deleteOnRestartManager = new TestDeleteOnRestartManager();
                var token = CancellationToken.None;
                var nuGetPackageManager = new NuGetPackageManager(
                    sourceRepositoryProvider,
                    testSettings,
                    testSolutionManager,
                    deleteOnRestartManager);
                var packagesFolderPath = PackagesFolderPathUtility.GetPackagesFolderPath(testSolutionManager, testSettings);

                var primarySourceRepository = sourceRepositoryProvider.GetRepositories().First();
                var msBuildNuGetProject = testSolutionManager.AddNewMSBuildProject();
                var msBuildNuGetProjectSystem = msBuildNuGetProject.MSBuildNuGetProjectSystem as TestMSBuildNuGetProjectSystem;
                var packagesConfigPath = msBuildNuGetProject.PackagesConfigNuGetProject.FullPath;
                var dotnetrdfPackageIdentity = new PackageIdentity("dotnetrdf", new NuGetVersion("1.0.8-prerelease1"));
                var resolutionContext = new ResolutionContext(DependencyBehavior.Highest, includePrelease: true, includeUnlisted: true, versionConstraints: VersionConstraints.None);

                var newtonsoftJsonPackageId = "newtonsoft.json";

                // Act
                var latestNewtonsoftPrereleaseVersion = await NuGetPackageManager.GetLatestVersionAsync(
                    newtonsoftJsonPackageId,
                    msBuildNuGetProject,
                    resolutionContext,
                    primarySourceRepository,
                    Common.NullLogger.Instance,
                    CancellationToken.None);

                var newtonsoftJsonPackageIdentity = new PackageIdentity(newtonsoftJsonPackageId, latestNewtonsoftPrereleaseVersion);

                var nuGetProjectActions = (await nuGetPackageManager.PreviewInstallPackageAsync(msBuildNuGetProject, dotnetrdfPackageIdentity, resolutionContext,
                    new TestNuGetProjectContext(), primarySourceRepository, null, CancellationToken.None)).ToList();

                // Assert
                Assert.Equal(4, nuGetProjectActions.Count);
                var newtonsoftJsonAction = nuGetProjectActions.Where(a => a.PackageIdentity.Equals(newtonsoftJsonPackageIdentity)).FirstOrDefault();

                Assert.NotNull(newtonsoftJsonAction);
            }
        }

        [Fact]
        public async Task TestPacManUpdateDependencyToPrereleaseVersion()
        {
            // Arrange
            var sourceRepositoryProvider = TestSourceRepositoryUtility.CreateV3OnlySourceRepositoryProvider();
            using (var testSolutionManager = new TestSolutionManager(true))
            {
                var testSettings = new Configuration.NullSettings();
                var deleteOnRestartManager = new TestDeleteOnRestartManager();
                var token = CancellationToken.None;
                var nuGetPackageManager = new NuGetPackageManager(
                    sourceRepositoryProvider,
                    testSettings,
                    testSolutionManager,
                    deleteOnRestartManager);
                var packagesFolderPath = PackagesFolderPathUtility.GetPackagesFolderPath(testSolutionManager, testSettings);

                var primarySourceRepository = sourceRepositoryProvider.GetRepositories().First();
                var msBuildNuGetProject = testSolutionManager.AddNewMSBuildProject();
                var msBuildNuGetProjectSystem = msBuildNuGetProject.MSBuildNuGetProjectSystem as TestMSBuildNuGetProjectSystem;
                var packagesConfigPath = msBuildNuGetProject.PackagesConfigNuGetProject.FullPath;
                var webgreasePackageIdentity = new PackageIdentity("WebGrease", new NuGetVersion("1.6.0"));
                var resolutionContext = new ResolutionContext(DependencyBehavior.Lowest, includePrelease: true, includeUnlisted: true, versionConstraints: VersionConstraints.None);

                var newtonsoftJsonPackageId = "newtonsoft.json";

                // Act
                var latestNewtonsoftPrereleaseVersion = await NuGetPackageManager.GetLatestVersionAsync(
                    newtonsoftJsonPackageId,
                    msBuildNuGetProject,
                    resolutionContext,
                    primarySourceRepository,
                    Common.NullLogger.Instance,
                    CancellationToken.None);

                var newtonsoftJsonLatestPrereleasePackageIdentity = new PackageIdentity(newtonsoftJsonPackageId, latestNewtonsoftPrereleaseVersion);

                await nuGetPackageManager.InstallPackageAsync(msBuildNuGetProject, webgreasePackageIdentity, resolutionContext,
                    new TestNuGetProjectContext(), primarySourceRepository, null, CancellationToken.None);

                // Assert
                // Check that the packages.config file exists after the installation
                Assert.True(File.Exists(packagesConfigPath));
                // Check the number of packages and packages returned by PackagesConfigProject after the installation
                var packagesInPackagesConfig = (await msBuildNuGetProject.PackagesConfigNuGetProject.GetInstalledPackagesAsync(token)).ToList();
                Assert.Equal(3, packagesInPackagesConfig.Count);

                // Main Act - Update newtonsoft.json to latest pre-release
                await nuGetPackageManager.InstallPackageAsync(msBuildNuGetProject, newtonsoftJsonLatestPrereleasePackageIdentity, resolutionContext,
                    new TestNuGetProjectContext(), primarySourceRepository, null, CancellationToken.None);
            }
        }

        [Fact]
        public async Task TestPacManPreviewInstallWithAllowedVersionsConstraint()
        {
            // Arrange
            var sourceRepositoryProvider = TestSourceRepositoryUtility.CreateV2OnlySourceRepositoryProvider();
            using (var testSolutionManager = new TestSolutionManager(true))
            {
                var testSettings = new Configuration.NullSettings();
                var deleteOnRestartManager = new TestDeleteOnRestartManager();
                var token = CancellationToken.None;
                var nuGetPackageManager = new NuGetPackageManager(
                    sourceRepositoryProvider,
                    testSettings,
                    testSolutionManager,
                    deleteOnRestartManager);
                var packagesFolderPath = PackagesFolderPathUtility.GetPackagesFolderPath(testSolutionManager, testSettings);

                var msBuildNuGetProject = testSolutionManager.AddNewMSBuildProject();
                var msBuildNuGetProjectSystem = msBuildNuGetProject.MSBuildNuGetProjectSystem as TestMSBuildNuGetProjectSystem;
                var packagesConfigPath = msBuildNuGetProject.PackagesConfigNuGetProject.FullPath;
                var newtonsoftJsonPackageId = "newtonsoft.json";
                var newtonsoftJsonPackageIdentity = new PackageIdentity(newtonsoftJsonPackageId, NuGetVersion.Parse("4.5.11"));
                var primarySourceRepository = sourceRepositoryProvider.GetRepositories().Single();
                var resolutionContext = new ResolutionContext();
                var testNuGetProjectContext = new TestNuGetProjectContext();

                // Act
                await nuGetPackageManager.InstallPackageAsync(msBuildNuGetProject, newtonsoftJsonPackageIdentity,
                    resolutionContext, testNuGetProjectContext, primarySourceRepository, null, token);

                // Assert
                // Check that the packages.config file exists after the installation
                Assert.True(File.Exists(packagesConfigPath));
                // Check the number of packages and packages returned by PackagesConfigProject after the installation
                var packagesInPackagesConfig = (await msBuildNuGetProject.PackagesConfigNuGetProject.GetInstalledPackagesAsync(token)).ToList();
                Assert.Equal(1, packagesInPackagesConfig.Count);
                Assert.Equal(newtonsoftJsonPackageIdentity, packagesInPackagesConfig[0].PackageIdentity);
                Assert.Equal(msBuildNuGetProject.MSBuildNuGetProjectSystem.TargetFramework, packagesInPackagesConfig[0].TargetFramework);
                var installedPackages = await msBuildNuGetProject.GetInstalledPackagesAsync(token);
                var newtonsoftJsonPackageReference = installedPackages.Where(pr => pr.PackageIdentity.Equals(newtonsoftJsonPackageIdentity)).FirstOrDefault();

                Assert.Null(newtonsoftJsonPackageReference.AllowedVersions);

                const string newPackagesConfig = @"<?xml version='1.0' encoding='utf-8'?>
  <packages>
    <package id='Newtonsoft.Json' version='4.5.11' allowedVersions='[4.0,5.0)' targetFramework='net45' />
  </packages> ";

                File.WriteAllText(packagesConfigPath, newPackagesConfig);

                // Check that the packages.config file exists after the installation
                Assert.True(File.Exists(packagesConfigPath));
                // Check the number of packages and packages returned by PackagesConfigProject after the installation
                packagesInPackagesConfig = (await msBuildNuGetProject.PackagesConfigNuGetProject.GetInstalledPackagesAsync(token)).ToList();
                Assert.Equal(1, packagesInPackagesConfig.Count);
                Assert.Equal(newtonsoftJsonPackageIdentity, packagesInPackagesConfig[0].PackageIdentity);
                Assert.Equal(msBuildNuGetProject.MSBuildNuGetProjectSystem.TargetFramework, packagesInPackagesConfig[0].TargetFramework);
                installedPackages = await msBuildNuGetProject.GetInstalledPackagesAsync(token);
                newtonsoftJsonPackageReference = installedPackages.Where(pr => pr.PackageIdentity.Equals(newtonsoftJsonPackageIdentity)).FirstOrDefault();

                Assert.NotNull(newtonsoftJsonPackageReference.AllowedVersions);

                Exception exception = null;
                try
                {
                    // Main Act
                    await nuGetPackageManager.PreviewInstallPackageAsync(msBuildNuGetProject, newtonsoftJsonPackageId,
                        resolutionContext, testNuGetProjectContext, primarySourceRepository, null, token);
                }
                catch (Exception ex)
                {
                    exception = ex;
                }

                Assert.NotNull(exception);
            }
        }

        [Fact]
        public async Task TestPacManPreviewUpdateWithAllowedVersionsConstraint()
        {
            // Arrange
            var sourceRepositoryProvider = TestSourceRepositoryUtility.CreateV2OnlySourceRepositoryProvider();
            using (var testSolutionManager = new TestSolutionManager(true))
            {
                var testSettings = new Configuration.NullSettings();
                var deleteOnRestartManager = new TestDeleteOnRestartManager();
                var token = CancellationToken.None;
                var nuGetPackageManager = new NuGetPackageManager(
                    sourceRepositoryProvider,
                    testSettings,
                    testSolutionManager,
                    deleteOnRestartManager);
                var packagesFolderPath = PackagesFolderPathUtility.GetPackagesFolderPath(testSolutionManager, testSettings);

                var msBuildNuGetProject = testSolutionManager.AddNewMSBuildProject();
                var msBuildNuGetProjectSystem = msBuildNuGetProject.MSBuildNuGetProjectSystem as TestMSBuildNuGetProjectSystem;
                var packagesConfigPath = msBuildNuGetProject.PackagesConfigNuGetProject.FullPath;
                var newtonsoftJsonPackageId = "newtonsoft.json";
                var newtonsoftJsonPackageIdentity = new PackageIdentity(newtonsoftJsonPackageId, NuGetVersion.Parse("4.5.11"));
                var primarySourceRepository = sourceRepositoryProvider.GetRepositories().Single();
                var resolutionContext = new ResolutionContext(DependencyBehavior.Highest, false, true, VersionConstraints.None);
                var testNuGetProjectContext = new TestNuGetProjectContext();

                // Act
                await nuGetPackageManager.InstallPackageAsync(msBuildNuGetProject, newtonsoftJsonPackageIdentity,
                    resolutionContext, testNuGetProjectContext, primarySourceRepository, null, token);

                await nuGetPackageManager.InstallPackageAsync(msBuildNuGetProject, new PackageIdentity("Microsoft.Web.Infrastructure", new NuGetVersion("1.0.0.0")),
                    resolutionContext, testNuGetProjectContext, primarySourceRepository, null, token);

                // Assert
                // Check that the packages.config file exists after the installation
                Assert.True(File.Exists(packagesConfigPath));
                // Check the number of packages and packages returned by PackagesConfigProject after the installation
                var packagesInPackagesConfig = (await msBuildNuGetProject.PackagesConfigNuGetProject.GetInstalledPackagesAsync(token)).ToList();
                Assert.Equal(2, packagesInPackagesConfig.Count);
                Assert.Equal(newtonsoftJsonPackageIdentity, packagesInPackagesConfig[1].PackageIdentity);
                Assert.Equal(msBuildNuGetProject.MSBuildNuGetProjectSystem.TargetFramework, packagesInPackagesConfig[1].TargetFramework);
                var installedPackages = await msBuildNuGetProject.GetInstalledPackagesAsync(token);
                var newtonsoftJsonPackageReference = installedPackages.Where(pr => pr.PackageIdentity.Equals(newtonsoftJsonPackageIdentity)).FirstOrDefault();

                Assert.Null(newtonsoftJsonPackageReference.AllowedVersions);

                const string newPackagesConfig = @"<?xml version='1.0' encoding='utf-8'?>
  <packages>
    <package id='Microsoft.Web.Infrastructure' version='1.0.0.0' targetFramework='net45' />
    <package id='Newtonsoft.Json' version='4.5.11' allowedVersions='[4.0,5.0)' targetFramework='net45' />
  </packages> ";

                File.WriteAllText(packagesConfigPath, newPackagesConfig);

                // Check that the packages.config file exists after the installation
                Assert.True(File.Exists(packagesConfigPath));
                // Check the number of packages and packages returned by PackagesConfigProject after the installation
                packagesInPackagesConfig = (await msBuildNuGetProject.PackagesConfigNuGetProject.GetInstalledPackagesAsync(token)).ToList();
                Assert.Equal(2, packagesInPackagesConfig.Count);
                Assert.Equal(newtonsoftJsonPackageIdentity, packagesInPackagesConfig[1].PackageIdentity);
                Assert.Equal(msBuildNuGetProject.MSBuildNuGetProjectSystem.TargetFramework, packagesInPackagesConfig[1].TargetFramework);
                installedPackages = await msBuildNuGetProject.GetInstalledPackagesAsync(token);
                newtonsoftJsonPackageReference = installedPackages.Where(pr => pr.PackageIdentity.Equals(newtonsoftJsonPackageIdentity)).FirstOrDefault();

                Assert.NotNull(newtonsoftJsonPackageReference.AllowedVersions);

                // Main Act
                var nuGetProjectActions = (await nuGetPackageManager.PreviewUpdatePackagesAsync(
                    msBuildNuGetProject,
                    resolutionContext,
                    testNuGetProjectContext,
                    sourceRepositoryProvider.GetRepositories(),
                    sourceRepositoryProvider.GetRepositories(),
                    token)).ToList();

                // Microsoft.Web.Infrastructure has no updates. However, newtonsoft.json has updates but does not satisfy the version range
                // Hence, no nuget project actions to perform
                Assert.Equal(0, nuGetProjectActions.Count);
            }
        }

        [Fact]
        public async Task TestPacManPreviewUpdateWithNoSource()
        {
            // Arrange
            var sourceRepositoryProvider = TestSourceRepositoryUtility.CreateSourceRepositoryProvider(new List<NuGet.Configuration.PackageSource>());
            using (var testSolutionManager = new TestSolutionManager(true))
            {
                var testSettings = new Configuration.NullSettings();
                var deleteOnRestartManager = new TestDeleteOnRestartManager();
                var token = CancellationToken.None;
                var nuGetPackageManager = new NuGetPackageManager(
                    sourceRepositoryProvider,
                    testSettings,
                    testSolutionManager,
                    deleteOnRestartManager);
                var packagesFolderPath = PackagesFolderPathUtility.GetPackagesFolderPath(testSolutionManager, testSettings);

                var msBuildNuGetProject = testSolutionManager.AddNewMSBuildProject();
                var msBuildNuGetProjectSystem = msBuildNuGetProject.MSBuildNuGetProjectSystem as TestMSBuildNuGetProjectSystem;
                var packagesConfigPath = msBuildNuGetProject.PackagesConfigNuGetProject.FullPath;
                var newtonsoftJsonPackageId = "newtonsoft.json";
                var newtonsoftJsonPackageIdentity = new PackageIdentity(newtonsoftJsonPackageId, NuGetVersion.Parse("4.5.11"));

                var resolutionContext = new ResolutionContext(DependencyBehavior.Highest, false, true, VersionConstraints.None);
                var testNuGetProjectContext = new TestNuGetProjectContext();

                // Act

                // Update ALL - this should not fail - it should no-op

                var nuGetProjectActions = (await nuGetPackageManager.PreviewUpdatePackagesAsync(
                    msBuildNuGetProject,
                    resolutionContext,
                    testNuGetProjectContext,
                    Enumerable.Empty<SourceRepository>(),
                    Enumerable.Empty<SourceRepository>(),
                    token)).ToList();

                // Hence, no nuget project actions to perform
                Assert.Equal(0, nuGetProjectActions.Count);
            }
        }

        [Fact]
        public async Task TestPacManInstallAspNetRazorJa()
        {
            // Arrange
            var sourceRepositoryProvider = TestSourceRepositoryUtility.CreateV3OnlySourceRepositoryProvider();
            using (var testSolutionManager = new TestSolutionManager(true))
            {
                var testSettings = new Configuration.NullSettings();
                var deleteOnRestartManager = new TestDeleteOnRestartManager();
                var token = CancellationToken.None;
                var nuGetPackageManager = new NuGetPackageManager(
                    sourceRepositoryProvider,
                    testSettings,
                    testSolutionManager,
                    deleteOnRestartManager);
                var packagesFolderPath = PackagesFolderPathUtility.GetPackagesFolderPath(testSolutionManager, testSettings);

                var msBuildNuGetProject = testSolutionManager.AddNewMSBuildProject();
                var msBuildNuGetProjectSystem = msBuildNuGetProject.MSBuildNuGetProjectSystem as TestMSBuildNuGetProjectSystem;
                var packagesConfigPath = msBuildNuGetProject.PackagesConfigNuGetProject.FullPath;
                var aspnetrazorjaPackageIdentity = new PackageIdentity("Microsoft.AspNet.Razor.ja", new NuGetVersion("3.2.3"));

                // Pre-Assert
                // Check that the packages.config file does not exist
                Assert.False(File.Exists(packagesConfigPath));
                // Check that there are no packages returned by PackagesConfigProject
                var packagesInPackagesConfig = (await msBuildNuGetProject.PackagesConfigNuGetProject.GetInstalledPackagesAsync(token)).ToList();
                Assert.Equal(0, packagesInPackagesConfig.Count);
                Assert.Equal(0, msBuildNuGetProjectSystem.References.Count);

                // Act
                await nuGetPackageManager.InstallPackageAsync(msBuildNuGetProject, aspnetrazorjaPackageIdentity,
                    new ResolutionContext(), new TestNuGetProjectContext(), sourceRepositoryProvider.GetRepositories().First(), null, token);

                // Assert
                // Check that the packages.config file exists after the installation
                Assert.True(File.Exists(packagesConfigPath));
                // Check the number of packages and packages returned by PackagesConfigProject after the installation
                packagesInPackagesConfig = (await msBuildNuGetProject.PackagesConfigNuGetProject.GetInstalledPackagesAsync(token)).ToList();
                Assert.Equal(2, packagesInPackagesConfig.Count);
                Assert.Equal(aspnetrazorjaPackageIdentity, packagesInPackagesConfig[1].PackageIdentity);
                Assert.Equal(msBuildNuGetProject.MSBuildNuGetProjectSystem.TargetFramework, packagesInPackagesConfig[1].TargetFramework);
            }
        }

        [Fact]
        public async Task TestPacManInstallMicrosoftWebInfrastructure1000FromV2()
        {
            // Arrange
            var sourceRepositoryProvider = TestSourceRepositoryUtility.CreateV2OnlySourceRepositoryProvider();
            using (var testSolutionManager = new TestSolutionManager(true))
            {
                var testSettings = new Configuration.NullSettings();
                var deleteOnRestartManager = new TestDeleteOnRestartManager();
                var token = CancellationToken.None;
                var nuGetPackageManager = new NuGetPackageManager(
                    sourceRepositoryProvider,
                    testSettings,
                    testSolutionManager,
                    deleteOnRestartManager);
                var packagesFolderPath = PackagesFolderPathUtility.GetPackagesFolderPath(testSolutionManager, testSettings);

                var msBuildNuGetProject = testSolutionManager.AddNewMSBuildProject();
                var msBuildNuGetProjectSystem = msBuildNuGetProject.MSBuildNuGetProjectSystem as TestMSBuildNuGetProjectSystem;
                var packagesConfigPath = msBuildNuGetProject.PackagesConfigNuGetProject.FullPath;
                var version = new NuGetVersion("1.0.0.0");
                var microsoftWebInfrastructurePackageIdentity = new PackageIdentity("Microsoft.Web.Infrastructure", version);

                // Pre-Assert
                // Check that the packages.config file does not exist
                Assert.False(File.Exists(packagesConfigPath));
                // Check that there are no packages returned by PackagesConfigProject
                var packagesInPackagesConfig = (await msBuildNuGetProject.PackagesConfigNuGetProject.GetInstalledPackagesAsync(token)).ToList();
                Assert.Equal(0, packagesInPackagesConfig.Count);
                Assert.Equal(0, msBuildNuGetProjectSystem.References.Count);

                // Act
                await nuGetPackageManager.InstallPackageAsync(msBuildNuGetProject, microsoftWebInfrastructurePackageIdentity,
                    new ResolutionContext(), new TestNuGetProjectContext(), sourceRepositoryProvider.GetRepositories().First(), null, token);

                // Assert
                // Check that the packages.config file exists after the installation
                Assert.True(File.Exists(packagesConfigPath));
                // Check the number of packages and packages returned by PackagesConfigProject after the installation
                packagesInPackagesConfig = (await msBuildNuGetProject.PackagesConfigNuGetProject.GetInstalledPackagesAsync(token)).ToList();
                Assert.Equal(1, packagesInPackagesConfig.Count);
                Assert.Equal(microsoftWebInfrastructurePackageIdentity, packagesInPackagesConfig[0].PackageIdentity);
                Assert.Equal(msBuildNuGetProject.MSBuildNuGetProjectSystem.TargetFramework, packagesInPackagesConfig[0].TargetFramework);

                var microsoftWebInfrastructure1000FolderPath = Path.Combine(packagesFolderPath, "Microsoft.Web.Infrastructure.1.0.0.0");
                Assert.True(Directory.Exists(microsoftWebInfrastructure1000FolderPath));
            }
        }

        [Fact]
        public async Task TestPacManInstallMicrosoftWebInfrastructure1000FromV3()
        {
            // Arrange
            var sourceRepositoryProvider = TestSourceRepositoryUtility.CreateV3OnlySourceRepositoryProvider();
            using (var testSolutionManager = new TestSolutionManager(true))
            {
                var testSettings = new Configuration.NullSettings();
                var deleteOnRestartManager = new TestDeleteOnRestartManager();
                var token = CancellationToken.None;
                var nuGetPackageManager = new NuGetPackageManager(
                    sourceRepositoryProvider,
                    testSettings,
                    testSolutionManager,
                    deleteOnRestartManager);
                var packagesFolderPath = PackagesFolderPathUtility.GetPackagesFolderPath(testSolutionManager, testSettings);

                var msBuildNuGetProject = testSolutionManager.AddNewMSBuildProject();
                var msBuildNuGetProjectSystem = msBuildNuGetProject.MSBuildNuGetProjectSystem as TestMSBuildNuGetProjectSystem;
                var packagesConfigPath = msBuildNuGetProject.PackagesConfigNuGetProject.FullPath;
                var version = new NuGetVersion("1.0.0.0");
                var microsoftWebInfrastructurePackageIdentity = new PackageIdentity("Microsoft.Web.Infrastructure", version);

                // Pre-Assert
                // Check that the packages.config file does not exist
                Assert.False(File.Exists(packagesConfigPath));
                // Check that there are no packages returned by PackagesConfigProject
                var packagesInPackagesConfig = (await msBuildNuGetProject.PackagesConfigNuGetProject.GetInstalledPackagesAsync(token)).ToList();
                Assert.Equal(0, packagesInPackagesConfig.Count);
                Assert.Equal(0, msBuildNuGetProjectSystem.References.Count);

                // Act
                await nuGetPackageManager.InstallPackageAsync(msBuildNuGetProject, microsoftWebInfrastructurePackageIdentity,
                    new ResolutionContext(), new TestNuGetProjectContext(), sourceRepositoryProvider.GetRepositories().First(), null, token);

                // Assert
                // Check that the packages.config file exists after the installation
                Assert.True(File.Exists(packagesConfigPath));
                // Check the number of packages and packages returned by PackagesConfigProject after the installation
                packagesInPackagesConfig = (await msBuildNuGetProject.PackagesConfigNuGetProject.GetInstalledPackagesAsync(token)).ToList();
                Assert.Equal(1, packagesInPackagesConfig.Count);
                Assert.Equal(microsoftWebInfrastructurePackageIdentity, packagesInPackagesConfig[0].PackageIdentity);
                Assert.Equal(msBuildNuGetProject.MSBuildNuGetProjectSystem.TargetFramework, packagesInPackagesConfig[0].TargetFramework);

                var microsoftWebInfrastructure1000FolderPath = Path.Combine(packagesFolderPath, "Microsoft.Web.Infrastructure.1.0.0.0");
                Assert.True(Directory.Exists(microsoftWebInfrastructure1000FolderPath));
            }
        }

        [Fact]
        public async Task TestPacManInstallElmah11FromV2()
        {
            // Arrange
            var sourceRepositoryProvider = TestSourceRepositoryUtility.CreateV2OnlySourceRepositoryProvider();
            using (var testSolutionManager = new TestSolutionManager(true))
            {
                var testSettings = new Configuration.NullSettings();
                var deleteOnRestartManager = new TestDeleteOnRestartManager();
                var token = CancellationToken.None;
                var nuGetPackageManager = new NuGetPackageManager(
                    sourceRepositoryProvider,
                    testSettings,
                    testSolutionManager,
                    deleteOnRestartManager);
                var packagesFolderPath = PackagesFolderPathUtility.GetPackagesFolderPath(testSolutionManager, testSettings);

                var msBuildNuGetProject = testSolutionManager.AddNewMSBuildProject();
                var msBuildNuGetProjectSystem = msBuildNuGetProject.MSBuildNuGetProjectSystem as TestMSBuildNuGetProjectSystem;
                var packagesConfigPath = msBuildNuGetProject.PackagesConfigNuGetProject.FullPath;
                var version = new NuGetVersion("1.1");
                var elmahPackageIdentity = new PackageIdentity("elmah", version);

                // Pre-Assert
                // Check that the packages.config file does not exist
                Assert.False(File.Exists(packagesConfigPath));
                // Check that there are no packages returned by PackagesConfigProject
                var packagesInPackagesConfig = (await msBuildNuGetProject.PackagesConfigNuGetProject.GetInstalledPackagesAsync(token)).ToList();
                Assert.Equal(0, packagesInPackagesConfig.Count);
                Assert.Equal(0, msBuildNuGetProjectSystem.References.Count);

                // Act
                await nuGetPackageManager.InstallPackageAsync(msBuildNuGetProject, elmahPackageIdentity,
                    new ResolutionContext(), new TestNuGetProjectContext(), sourceRepositoryProvider.GetRepositories().First(), null, token);

                // Assert
                // Check that the packages.config file exists after the installation
                Assert.True(File.Exists(packagesConfigPath));
                // Check the number of packages and packages returned by PackagesConfigProject after the installation
                packagesInPackagesConfig = (await msBuildNuGetProject.PackagesConfigNuGetProject.GetInstalledPackagesAsync(token)).ToList();
                Assert.Equal(1, packagesInPackagesConfig.Count);
                Assert.Equal(elmahPackageIdentity, packagesInPackagesConfig[0].PackageIdentity);
                Assert.Equal(msBuildNuGetProject.MSBuildNuGetProjectSystem.TargetFramework, packagesInPackagesConfig[0].TargetFramework);

                var microsoftWebInfrastructure1000FolderPath = Path.Combine(packagesFolderPath, "elmah.1.1");
                Assert.True(Directory.Exists(microsoftWebInfrastructure1000FolderPath));
            }
        }

        [Fact]
        public async Task TestPacManInstallElmah11FromV3()
        {
            // Arrange
            var sourceRepositoryProvider = TestSourceRepositoryUtility.CreateV3OnlySourceRepositoryProvider();
            using (var testSolutionManager = new TestSolutionManager(true))
            {
                var testSettings = new Configuration.NullSettings();
                var deleteOnRestartManager = new TestDeleteOnRestartManager();
                var token = CancellationToken.None;
                var nuGetPackageManager = new NuGetPackageManager(
                    sourceRepositoryProvider,
                    testSettings,
                    testSolutionManager,
                    deleteOnRestartManager);
                var packagesFolderPath = PackagesFolderPathUtility.GetPackagesFolderPath(testSolutionManager, testSettings);

                var msBuildNuGetProject = testSolutionManager.AddNewMSBuildProject();
                var msBuildNuGetProjectSystem = msBuildNuGetProject.MSBuildNuGetProjectSystem as TestMSBuildNuGetProjectSystem;
                var packagesConfigPath = msBuildNuGetProject.PackagesConfigNuGetProject.FullPath;
                var version = new NuGetVersion("1.1");
                var elmahPackageIdentity = new PackageIdentity("elmah", version);

                // Pre-Assert
                // Check that the packages.config file does not exist
                Assert.False(File.Exists(packagesConfigPath));
                // Check that there are no packages returned by PackagesConfigProject
                var packagesInPackagesConfig = (await msBuildNuGetProject.PackagesConfigNuGetProject.GetInstalledPackagesAsync(token)).ToList();
                Assert.Equal(0, packagesInPackagesConfig.Count);
                Assert.Equal(0, msBuildNuGetProjectSystem.References.Count);

                // Act
                await nuGetPackageManager.InstallPackageAsync(msBuildNuGetProject, elmahPackageIdentity,
                    new ResolutionContext(), new TestNuGetProjectContext(), sourceRepositoryProvider.GetRepositories().First(), null, token);

                // Assert
                // Check that the packages.config file exists after the installation
                Assert.True(File.Exists(packagesConfigPath));
                // Check the number of packages and packages returned by PackagesConfigProject after the installation
                packagesInPackagesConfig = (await msBuildNuGetProject.PackagesConfigNuGetProject.GetInstalledPackagesAsync(token)).ToList();
                Assert.Equal(1, packagesInPackagesConfig.Count);
                Assert.Equal(elmahPackageIdentity, packagesInPackagesConfig[0].PackageIdentity);
                Assert.Equal(msBuildNuGetProject.MSBuildNuGetProjectSystem.TargetFramework, packagesInPackagesConfig[0].TargetFramework);

                var microsoftWebInfrastructure1000FolderPath = Path.Combine(packagesFolderPath, "elmah.1.1");
                Assert.True(Directory.Exists(microsoftWebInfrastructure1000FolderPath));
            }
        }

        [Fact]
        public async Task TestPacManInstall_SharpDX_DXGI_v263_WithNonReferencesInLibFolder()
        {
            // Arrange
            var sourceRepositoryProvider = TestSourceRepositoryUtility.CreateV3OnlySourceRepositoryProvider();
            using (var testSolutionManager = new TestSolutionManager(true))
            {
                var testSettings = new Configuration.NullSettings();
                var deleteOnRestartManager = new TestDeleteOnRestartManager();
                var token = CancellationToken.None;
                var nuGetPackageManager = new NuGetPackageManager(
                    sourceRepositoryProvider,
                    testSettings,
                    testSolutionManager,
                    deleteOnRestartManager);
                var packagesFolderPath = PackagesFolderPathUtility.GetPackagesFolderPath(testSolutionManager, testSettings);

                var msBuildNuGetProject = testSolutionManager.AddNewMSBuildProject();
                var msBuildNuGetProjectSystem = msBuildNuGetProject.MSBuildNuGetProjectSystem as TestMSBuildNuGetProjectSystem;
                var packagesConfigPath = msBuildNuGetProject.PackagesConfigNuGetProject.FullPath;
                var version = new NuGetVersion("2.6.3");
                var sharpDXDXGIv263Package = new PackageIdentity("SharpDX.DXGI", version);

                // Pre-Assert
                // Check that the packages.config file does not exist
                Assert.False(File.Exists(packagesConfigPath));
                // Check that there are no packages returned by PackagesConfigProject
                var packagesInPackagesConfig = (await msBuildNuGetProject.PackagesConfigNuGetProject.GetInstalledPackagesAsync(token)).ToList();
                Assert.Equal(0, packagesInPackagesConfig.Count);
                Assert.Equal(0, msBuildNuGetProjectSystem.References.Count);

                // Act
                await nuGetPackageManager.InstallPackageAsync(msBuildNuGetProject, sharpDXDXGIv263Package,
                    new ResolutionContext(), new TestNuGetProjectContext(), sourceRepositoryProvider.GetRepositories().First(), null, token);

                // Assert
                // Check that the packages.config file exists after the installation
                Assert.True(File.Exists(packagesConfigPath));
                // Check the number of packages and packages returned by PackagesConfigProject after the installation
                packagesInPackagesConfig = (await msBuildNuGetProject.PackagesConfigNuGetProject.GetInstalledPackagesAsync(token)).ToList();
                Assert.Equal(2, packagesInPackagesConfig.Count);
                Assert.True(packagesInPackagesConfig.Where(p => p.PackageIdentity.Equals(sharpDXDXGIv263Package)).Any());
            }
        }

        [Fact]
        public async Task TestPacManInstallPackageUnlistedFromV3()
        {
            // Arrange
            var packages = new List<SourcePackageDependencyInfo>
            {
                new SourcePackageDependencyInfo("a", new NuGetVersion(1, 0, 0), new[] { new Packaging.Core.PackageDependency("b", new VersionRange(new NuGetVersion(1, 0, 0))) }, true, null),
                new SourcePackageDependencyInfo("b", new NuGetVersion(1, 0, 0), new Packaging.Core.PackageDependency[] { }, false, null),
                new SourcePackageDependencyInfo("b", new NuGetVersion(2, 0, 0), new Packaging.Core.PackageDependency[] { }, false, null),
                new SourcePackageDependencyInfo("b", new NuGetVersion(3, 0, 0), new Packaging.Core.PackageDependency[] { }, true, null),
            };

            var resourceProviders = new List<Lazy<INuGetResourceProvider>>();
            resourceProviders.Add(new Lazy<INuGetResourceProvider>(() => new TestDependencyInfoProvider(packages)));
            resourceProviders.Add(new Lazy<INuGetResourceProvider>(() => new TestMetadataProvider(packages)));

            var packageSource = new Configuration.PackageSource("http://a");
            var packageSourceProvider = new TestPackageSourceProvider(new[] { packageSource });

            var sourceRepositoryProvider = new SourceRepositoryProvider(packageSourceProvider, resourceProviders);

            using (var testSolutionManager = new TestSolutionManager(true))
            {
                var testSettings = new Configuration.NullSettings();
                var deleteOnRestartManager = new TestDeleteOnRestartManager();
                var token = CancellationToken.None;
                var nuGetPackageManager = new NuGetPackageManager(
                    sourceRepositoryProvider,
                    testSettings,
                    testSolutionManager,
                    deleteOnRestartManager);
                var packagesFolderPath = PackagesFolderPathUtility.GetPackagesFolderPath(testSolutionManager, testSettings);

                var msBuildNuGetProject = testSolutionManager.AddNewMSBuildProject();
                var msBuildNuGetProjectSystem = msBuildNuGetProject.MSBuildNuGetProjectSystem as TestMSBuildNuGetProjectSystem;
                var packagesConfigPath = msBuildNuGetProject.PackagesConfigNuGetProject.FullPath;
                var target = "a";

                // Pre-Assert
                // Check that the packages.config file does not exist
                Assert.False(File.Exists(packagesConfigPath));
                // Check that there are no packages returned by PackagesConfigProject
                var packagesInPackagesConfig = (await msBuildNuGetProject.PackagesConfigNuGetProject.GetInstalledPackagesAsync(token)).ToList();
                Assert.Equal(0, packagesInPackagesConfig.Count);
                Assert.Equal(0, msBuildNuGetProjectSystem.References.Count);

                // Act
                var nugetProjectActions = await nuGetPackageManager.PreviewInstallPackageAsync(msBuildNuGetProject, target,
                    new ResolutionContext(), new TestNuGetProjectContext(), sourceRepositoryProvider.GetRepositories().First(), null, token);

                var result = nugetProjectActions.ToList();

                var resultIdentities = result.Select(p => p.PackageIdentity);

                Assert.True(resultIdentities.Contains(new PackageIdentity("a", new NuGetVersion(1, 0, 0))));
                Assert.True(resultIdentities.Contains(new PackageIdentity("b", new NuGetVersion(3, 0, 0))));

                //  and all the actions are Install
                foreach (var nugetProjectAction in result)
                {
                    Assert.Equal(nugetProjectAction.NuGetProjectActionType, NuGetProjectActionType.Install);
                }
            }
        }

        [Fact]
        public async Task TestPacManInstallPackageListedFromV3()
        {
            // Arrange
            var packages = new List<SourcePackageDependencyInfo>
            {
                new SourcePackageDependencyInfo("a", new NuGetVersion(1, 0, 0), new[] { new Packaging.Core.PackageDependency("b", new VersionRange(new NuGetVersion(1, 0, 0))) }, true, null),
                new SourcePackageDependencyInfo("a", new NuGetVersion(2, 0, 0), new[] { new Packaging.Core.PackageDependency("b", new VersionRange(new NuGetVersion(1, 0, 0))) }, true, null),
                new SourcePackageDependencyInfo("b", new NuGetVersion(1, 0, 0), new Packaging.Core.PackageDependency[] { }, true, null),
                new SourcePackageDependencyInfo("b", new NuGetVersion(2, 0, 0), new Packaging.Core.PackageDependency[] { }, true, null),
                new SourcePackageDependencyInfo("b", new NuGetVersion(3, 0, 0), new Packaging.Core.PackageDependency[] { }, true, null),
            };

            var resourceProviders = new List<Lazy<INuGetResourceProvider>>();
            resourceProviders.Add(new Lazy<INuGetResourceProvider>(() => new TestDependencyInfoProvider(packages)));
            resourceProviders.Add(new Lazy<INuGetResourceProvider>(() => new TestMetadataProvider(packages)));

            var packageSource = new Configuration.PackageSource("http://a");
            var packageSourceProvider = new TestPackageSourceProvider(new[] { packageSource });

            var sourceRepositoryProvider = new SourceRepositoryProvider(packageSourceProvider, resourceProviders);

            using (var testSolutionManager = new TestSolutionManager(true))
            {
                var testSettings = new Configuration.NullSettings();
                var deleteOnRestartManager = new TestDeleteOnRestartManager();
                var token = CancellationToken.None;
                var nuGetPackageManager = new NuGetPackageManager(
                    sourceRepositoryProvider,
                    testSettings,
                    testSolutionManager,
                    deleteOnRestartManager);
                var packagesFolderPath = PackagesFolderPathUtility.GetPackagesFolderPath(testSolutionManager, testSettings);

                var msBuildNuGetProject = testSolutionManager.AddNewMSBuildProject();
                var msBuildNuGetProjectSystem = msBuildNuGetProject.MSBuildNuGetProjectSystem as TestMSBuildNuGetProjectSystem;
                var packagesConfigPath = msBuildNuGetProject.PackagesConfigNuGetProject.FullPath;
                var target = "a";

                // Act
                var nugetProjectActions = await nuGetPackageManager.PreviewInstallPackageAsync(msBuildNuGetProject, target,
                    new ResolutionContext(), new TestNuGetProjectContext(), sourceRepositoryProvider.GetRepositories().First(), null, token);

                var result = nugetProjectActions.ToList();

                var resultIdentities = result.Select(p => p.PackageIdentity);

                Assert.True(resultIdentities.Contains(new PackageIdentity("a", new NuGetVersion(2, 0, 0))));
                Assert.True(resultIdentities.Contains(new PackageIdentity("b", new NuGetVersion(1, 0, 0))));

                //  and all the actions are Install
                foreach (var nugetProjectAction in result)
                {
                    Assert.Equal(nugetProjectAction.NuGetProjectActionType, NuGetProjectActionType.Install);
                }
            }
        }

        [Fact]
        public async Task TestPacManInstallPackage571FromV3()
        {
            // Arrange
            var sourceRepositoryProvider = TestSourceRepositoryUtility.CreateV3OnlySourceRepositoryProvider();

            using (var testSolutionManager = new TestSolutionManager(true))
            {
                var testSettings = new Configuration.NullSettings();
                var deleteOnRestartManager = new TestDeleteOnRestartManager();
                var token = CancellationToken.None;
                var nuGetPackageManager = new NuGetPackageManager(
                    sourceRepositoryProvider,
                    testSettings,
                    testSolutionManager,
                    deleteOnRestartManager);
                var packagesFolderPath = PackagesFolderPathUtility.GetPackagesFolderPath(testSolutionManager, testSettings);

                var msBuildNuGetProject = testSolutionManager.AddNewMSBuildProject();
                var msBuildNuGetProjectSystem = msBuildNuGetProject.MSBuildNuGetProjectSystem as TestMSBuildNuGetProjectSystem;
                var packagesConfigPath = msBuildNuGetProject.PackagesConfigNuGetProject.FullPath;
                var target = new PackageIdentity("Umbraco", NuGetVersion.Parse("5.1.0.175"));

                // Act
                var nugetProjectActions = await nuGetPackageManager.PreviewInstallPackageAsync(msBuildNuGetProject, target,
                    new ResolutionContext(), new TestNuGetProjectContext(), sourceRepositoryProvider.GetRepositories().First(), null, token);

                var result = nugetProjectActions.ToList();

                var resultIdentities = result.Select(p => p.PackageIdentity);

                Assert.True(resultIdentities.Contains(new PackageIdentity("Umbraco", new NuGetVersion("5.1.0.175"))));

                //  and all the actions are Install
                foreach (var nugetProjectAction in result)
                {
                    Assert.Equal(nugetProjectAction.NuGetProjectActionType, NuGetProjectActionType.Install);
                }
            }
        }

        [Fact]
        public async Task TestPacManInstallPackageEFFromV3()
        {
            // Arrange
            //var sourceRepositoryProvider = TestSourceRepositoryUtility.CreateV3OnlySourceRepositoryProvider();

            var sourceRepositoryProvider = TestSourceRepositoryUtility.CreateSourceRepositoryProvider(new[]
            {
                TestSourceRepositoryUtility.V3PackageSource,
                new NuGet.Configuration.PackageSource("https://www.myget.org/F/aspnetvnext/api/v2/"),
            });

            using (var testSolutionManager = new TestSolutionManager(true))
            {
                var testSettings = new Configuration.NullSettings();
                var deleteOnRestartManager = new TestDeleteOnRestartManager();
                var token = CancellationToken.None;
                var nuGetPackageManager = new NuGetPackageManager(
                    sourceRepositoryProvider,
                    testSettings,
                    testSolutionManager,
                    deleteOnRestartManager);
                var packagesFolderPath = PackagesFolderPathUtility.GetPackagesFolderPath(testSolutionManager, testSettings);

                var msBuildNuGetProject = testSolutionManager.AddNewMSBuildProject("TestProject", NuGetFramework.Parse("net452"));
                var target = new PackageIdentity("EntityFramework", NuGetVersion.Parse("7.0.0-beta4"));

                // Act
                var nugetProjectActions = await nuGetPackageManager.PreviewInstallPackageAsync(
                    msBuildNuGetProject,
                    target,
                    new ResolutionContext(DependencyBehavior.Lowest, true, false, VersionConstraints.None),
                    new TestNuGetProjectContext(),
                    sourceRepositoryProvider.GetRepositories().First(),
                    sourceRepositoryProvider.GetRepositories(),
                    token);

                var result = nugetProjectActions.ToList();

                var resultIdentities = result.Select(p => p.PackageIdentity);

                Assert.True(resultIdentities.Contains(target));

                //  and all the actions are Install
                foreach (var nugetProjectAction in result)
                {
                    Assert.Equal(nugetProjectAction.NuGetProjectActionType, NuGetProjectActionType.Install);
                }
            }
        }

        [Fact]
        public async Task TestPacManInstallPackagePrereleaseDependenciesFromV2()
        {
            // Arrange
            var sourceRepositoryProvider = TestSourceRepositoryUtility.CreateV2OnlySourceRepositoryProvider();

            using (var testSolutionManager = new TestSolutionManager(true))
            {
                var testSettings = new Configuration.NullSettings();
                var deleteOnRestartManager = new TestDeleteOnRestartManager();
                var token = CancellationToken.None;
                var nuGetPackageManager = new NuGetPackageManager(
                    sourceRepositoryProvider,
                    testSettings,
                    testSolutionManager,
                    deleteOnRestartManager);
                var packagesFolderPath = PackagesFolderPathUtility.GetPackagesFolderPath(testSolutionManager, testSettings);

                var msBuildNuGetProject = testSolutionManager.AddNewMSBuildProject();
                var msBuildNuGetProjectSystem = msBuildNuGetProject.MSBuildNuGetProjectSystem as TestMSBuildNuGetProjectSystem;
                var packagesConfigPath = msBuildNuGetProject.PackagesConfigNuGetProject.FullPath;
                var target = new PackageIdentity("DependencyTestA", NuGetVersion.Parse("1.0.0"));

                // Act
                var nugetProjectActions = await nuGetPackageManager.PreviewInstallPackageAsync(
                    msBuildNuGetProject,
                    target,
                    new ResolutionContext(DependencyBehavior.Lowest, false, false, VersionConstraints.None),
                    new TestNuGetProjectContext(),
                    sourceRepositoryProvider.GetRepositories().First(),
                    null,
                    token);

                var result = nugetProjectActions.ToList();

                var resultIdentities = result.Select(p => p.PackageIdentity);

                Assert.True(resultIdentities.Contains(target));
                Assert.True(resultIdentities.Contains(new PackageIdentity("DependencyTestB", NuGetVersion.Parse("1.0.0"))));

                //  and all the actions are Install
                foreach (var nugetProjectAction in result)
                {
                    Assert.Equal(nugetProjectAction.NuGetProjectActionType, NuGetProjectActionType.Install);
                }
            }
        }

        [Fact]
        public async Task TestPacManInstallPackagePrereleaseDependenciesFromV2IncludePrerelease()
        {
            // Arrange
            var sourceRepositoryProvider = TestSourceRepositoryUtility.CreateV2OnlySourceRepositoryProvider();

            using (var testSolutionManager = new TestSolutionManager(true))
            {
                var testSettings = new Configuration.NullSettings();
                var deleteOnRestartManager = new TestDeleteOnRestartManager();
                var token = CancellationToken.None;
                var nuGetPackageManager = new NuGetPackageManager(
                    sourceRepositoryProvider,
                    testSettings,
                    testSolutionManager,
                    deleteOnRestartManager);
                var packagesFolderPath = PackagesFolderPathUtility.GetPackagesFolderPath(testSolutionManager, testSettings);

                var msBuildNuGetProject = testSolutionManager.AddNewMSBuildProject();
                var msBuildNuGetProjectSystem = msBuildNuGetProject.MSBuildNuGetProjectSystem as TestMSBuildNuGetProjectSystem;
                var packagesConfigPath = msBuildNuGetProject.PackagesConfigNuGetProject.FullPath;
                var target = new PackageIdentity("DependencyTestA", NuGetVersion.Parse("1.0.0"));

                // Act
                var nugetProjectActions = await nuGetPackageManager.PreviewInstallPackageAsync(
                    msBuildNuGetProject,
                    target,
                    new ResolutionContext(DependencyBehavior.Lowest, true, false, VersionConstraints.None),
                    new TestNuGetProjectContext(),
                    sourceRepositoryProvider.GetRepositories().First(),
                    null,
                    token);

                var result = nugetProjectActions.ToList();

                var resultIdentities = result.Select(p => p.PackageIdentity);

                Assert.True(resultIdentities.Contains(target));
                Assert.True(resultIdentities.Contains(new PackageIdentity("DependencyTestB", NuGetVersion.Parse("1.0.0-a"))));

                //  and all the actions are Install
                foreach (var nugetProjectAction in result)
                {
                    Assert.Equal(nugetProjectAction.NuGetProjectActionType, NuGetProjectActionType.Install);
                }
            }
        }

        [Fact]
        public async Task TestPacManInstallPackagePrerelease()
        {
            // Arrange
            var sourceRepositoryProvider = TestSourceRepositoryUtility.CreateV2OnlySourceRepositoryProvider();

            using (var testSolutionManager = new TestSolutionManager(true))
            {
                var testSettings = new Configuration.NullSettings();
                var deleteOnRestartManager = new TestDeleteOnRestartManager();
                var token = CancellationToken.None;
                var nuGetPackageManager = new NuGetPackageManager(
                    sourceRepositoryProvider,
                    testSettings,
                    testSolutionManager,
                    deleteOnRestartManager); new NuGetPackageManager(
                     sourceRepositoryProvider,
                     testSettings,
                     testSolutionManager,
                     deleteOnRestartManager);
                var packagesFolderPath = PackagesFolderPathUtility.GetPackagesFolderPath(testSolutionManager, testSettings);

                var msBuildNuGetProject = testSolutionManager.AddNewMSBuildProject();
                var msBuildNuGetProjectSystem = msBuildNuGetProject.MSBuildNuGetProjectSystem as TestMSBuildNuGetProjectSystem;
                var packagesConfigPath = msBuildNuGetProject.PackagesConfigNuGetProject.FullPath;
                var target = new PackageIdentity("Microsoft.ApplicationInsights.Web", NuGetVersion.Parse("0.16.1-build00418"));

                // Act
                var nugetProjectActions = await nuGetPackageManager.PreviewInstallPackageAsync(
                    msBuildNuGetProject,
                    target,
                    new ResolutionContext(DependencyBehavior.Lowest, false, false, VersionConstraints.None),
                    new TestNuGetProjectContext(),
                    sourceRepositoryProvider.GetRepositories().First(),
                    null,
                    token);

                var result = nugetProjectActions.ToList();

                var resultIdentities = result.Select(p => p.PackageIdentity);

                Assert.True(resultIdentities.Contains(target));

                //  and all the actions are Install
                foreach (var nugetProjectAction in result)
                {
                    Assert.Equal(nugetProjectAction.NuGetProjectActionType, NuGetProjectActionType.Install);
                }
            }
        }

        [Fact]
        public async Task TestPacManInstallPackageOverExisting()
        {
            // Arrange
            var fwk46 = NuGetFramework.Parse("net46");
            var fwk45 = NuGetFramework.Parse("net45");
            var fwk4 = NuGetFramework.Parse("net4");

            var installedPackages = new List<NuGet.Packaging.PackageReference>
            {
                new NuGet.Packaging.PackageReference(new PackageIdentity("51Degrees.mobi", NuGetVersion.Parse("2.1.15.1")), fwk4, true),
                new NuGet.Packaging.PackageReference(new PackageIdentity("AspNetMvc", NuGetVersion.Parse("4.0.20710.0")), fwk45, true),
                new NuGet.Packaging.PackageReference(new PackageIdentity("AttributeRouting", NuGetVersion.Parse("3.5.6")), fwk4, true),
                new NuGet.Packaging.PackageReference(new PackageIdentity("AttributeRouting.Core", NuGetVersion.Parse("3.5.6")), fwk4, true),
                new NuGet.Packaging.PackageReference(new PackageIdentity("AttributeRouting.Core.Web", NuGetVersion.Parse("3.5.6")), fwk4, true),
                new NuGet.Packaging.PackageReference(new PackageIdentity("AutoMapper", NuGetVersion.Parse("3.3.1")), fwk45, true),
                new NuGet.Packaging.PackageReference(new PackageIdentity("Castle.Core", NuGetVersion.Parse("1.1.0")), fwk4, true),
                new NuGet.Packaging.PackageReference(new PackageIdentity("Castle.DynamicProxy", NuGetVersion.Parse("2.1.0")), fwk4, true),
                new NuGet.Packaging.PackageReference(new PackageIdentity("Clay", NuGetVersion.Parse("1.0")), fwk4, true),
                new NuGet.Packaging.PackageReference(new PackageIdentity("colorbox", NuGetVersion.Parse("1.4.29")), fwk45, true),
                new NuGet.Packaging.PackageReference(new PackageIdentity("elmah", NuGetVersion.Parse("1.2.0.1")), fwk4, true),
                new NuGet.Packaging.PackageReference(new PackageIdentity("elmah.corelibrary", NuGetVersion.Parse("1.2")), fwk4, true),
                new NuGet.Packaging.PackageReference(new PackageIdentity("EntityFramework", NuGetVersion.Parse("6.1.3")), fwk45, true),
                new NuGet.Packaging.PackageReference(new PackageIdentity("fasterflect", NuGetVersion.Parse("2.1.0")), fwk4, true),
                new NuGet.Packaging.PackageReference(new PackageIdentity("foolproof", NuGetVersion.Parse("0.9.4517")), fwk45, true),
                new NuGet.Packaging.PackageReference(new PackageIdentity("Glimpse", NuGetVersion.Parse("0.87")), fwk4, true),
                new NuGet.Packaging.PackageReference(new PackageIdentity("Glimpse.Elmah", NuGetVersion.Parse("0.9.3")), fwk4, true),
                new NuGet.Packaging.PackageReference(new PackageIdentity("Glimpse.Mvc3", NuGetVersion.Parse("0.87")), fwk4, true),
                new NuGet.Packaging.PackageReference(new PackageIdentity("jQuery", NuGetVersion.Parse("1.4.1")), fwk45, true),
                new NuGet.Packaging.PackageReference(new PackageIdentity("knockout.mapper.TypeScript.DefinitelyTyped", NuGetVersion.Parse("0.0.4")), fwk45, true),
                new NuGet.Packaging.PackageReference(new PackageIdentity("Knockout.Mapping", NuGetVersion.Parse("2.4.0")), fwk45, true),
                new NuGet.Packaging.PackageReference(new PackageIdentity("knockout.mapping.TypeScript.DefinitelyTyped", NuGetVersion.Parse("0.0.9")), fwk45, true),
                new NuGet.Packaging.PackageReference(new PackageIdentity("knockout.TypeScript.DefinitelyTyped", NuGetVersion.Parse("0.5.1")), fwk45, true),
                new NuGet.Packaging.PackageReference(new PackageIdentity("Knockout.Validation", NuGetVersion.Parse("1.0.1")), fwk45, true),
                new NuGet.Packaging.PackageReference(new PackageIdentity("knockoutjs", NuGetVersion.Parse("2.0.0")), fwk45, true),
                new NuGet.Packaging.PackageReference(new PackageIdentity("LINQtoCSV", NuGetVersion.Parse("1.2.0.0")), fwk4, true),
                new NuGet.Packaging.PackageReference(new PackageIdentity("log4net", NuGetVersion.Parse("2.0.3")), fwk45, true),
                new NuGet.Packaging.PackageReference(new PackageIdentity("Microsoft.AspNet.Mvc", NuGetVersion.Parse("4.0.40804.0")), fwk45, true),
                new NuGet.Packaging.PackageReference(new PackageIdentity("Microsoft.AspNet.Razor", NuGetVersion.Parse("2.0.30506.0")), fwk45, true),
                new NuGet.Packaging.PackageReference(new PackageIdentity("Microsoft.AspNet.WebPages", NuGetVersion.Parse("2.0.30506.0")), fwk45, true),
                new NuGet.Packaging.PackageReference(new PackageIdentity("Microsoft.Web.Infrastructure", NuGetVersion.Parse("1.0.0.0")), fwk4, true),
                new NuGet.Packaging.PackageReference(new PackageIdentity("MiniProfiler", NuGetVersion.Parse("3.1.1.140")), fwk45, true),
                new NuGet.Packaging.PackageReference(new PackageIdentity("MiniProfiler.EF6", NuGetVersion.Parse("3.0.11")), fwk45, true),
                new NuGet.Packaging.PackageReference(new PackageIdentity("MiniProfiler.MVC4", NuGetVersion.Parse("3.0.11")), fwk45, true),
                new NuGet.Packaging.PackageReference(new PackageIdentity("Mvc3CodeTemplatesCSharp", NuGetVersion.Parse("3.0.11214.0")), fwk4, true),
                new NuGet.Packaging.PackageReference(new PackageIdentity("MvcDiagnostics", NuGetVersion.Parse("3.0.10714.0")), fwk4, true),
                new NuGet.Packaging.PackageReference(new PackageIdentity("Newtonsoft.Json", NuGetVersion.Parse("6.0.8")), fwk45, true),
                new NuGet.Packaging.PackageReference(new PackageIdentity("Ninject", NuGetVersion.Parse("3.2.2.0")), fwk45, true),
                new NuGet.Packaging.PackageReference(new PackageIdentity("Ninject.Web.Common", NuGetVersion.Parse("3.2.3.0")), fwk45, true),
                new NuGet.Packaging.PackageReference(new PackageIdentity("OpenPop.NET", NuGetVersion.Parse("2.0.5.1063")), fwk45, true),
                new NuGet.Packaging.PackageReference(new PackageIdentity("PreMailer.Net", NuGetVersion.Parse("1.1.2")), fwk4, true),
                new NuGet.Packaging.PackageReference(new PackageIdentity("Rejuicer", NuGetVersion.Parse("1.3.0")), fwk45, true),
                new NuGet.Packaging.PackageReference(new PackageIdentity("T4MVCExtensions", NuGetVersion.Parse("3.15.2")), fwk46, true),
                new NuGet.Packaging.PackageReference(new PackageIdentity("T4MvcJs", NuGetVersion.Parse("1.0.13")), fwk45, true),
                new NuGet.Packaging.PackageReference(new PackageIdentity("Twia.ReSharper", NuGetVersion.Parse("9.0.0")), fwk45, true),
                new NuGet.Packaging.PackageReference(new PackageIdentity("valueinjecter", NuGetVersion.Parse("2.3.3")), fwk45, true),
                new NuGet.Packaging.PackageReference(new PackageIdentity("WebActivator", NuGetVersion.Parse("1.5")), fwk4, true),
                new NuGet.Packaging.PackageReference(new PackageIdentity("YUICompressor.NET", NuGetVersion.Parse("1.6.0.2")), fwk45, true),
            };

            var nuGetProject = new TestNuGetProject(installedPackages);

            var target = "t4mvc";

            // Act
            var sourceRepositoryProvider = TestSourceRepositoryUtility.CreateV2OnlySourceRepositoryProvider();
            var testSettings = new NuGet.Configuration.NullSettings();
            using (var testSolutionManager = new TestSolutionManager(true))
            {
                var deleteOnRestartManager = new TestDeleteOnRestartManager();

                var nuGetPackageManager = new NuGetPackageManager(
                    sourceRepositoryProvider,
                    testSettings,
                    testSolutionManager,
                    deleteOnRestartManager);

                var nugetProjectActions = await nuGetPackageManager.PreviewInstallPackageAsync(
                    nuGetProject,
                    target,
                    new ResolutionContext(DependencyBehavior.Lowest, false, false, VersionConstraints.None),
                    new TestNuGetProjectContext(),
                    sourceRepositoryProvider.GetRepositories().First(),
                    null,
                    CancellationToken.None);

                Assert.True(nugetProjectActions.Select(pa => pa.PackageIdentity.Id).Contains(target, StringComparer.OrdinalIgnoreCase));
            }
        }

        [Fact(Skip = "Test was skipped as part of 475ad399 and is currently broken.")]
        public async Task TestPacManInstallPackageDowngrade()
        {
            // Arrange
            var fwk46 = NuGetFramework.Parse("net46");
            var fwk45 = NuGetFramework.Parse("net45");
            var fwk4 = NuGetFramework.Parse("net4");

            var installedPackages = new List<NuGet.Packaging.PackageReference>
            {
                new NuGet.Packaging.PackageReference(new PackageIdentity("ResolverTestA", NuGetVersion.Parse("3.0.0")), fwk45, true),
                new NuGet.Packaging.PackageReference(new PackageIdentity("ResolverTestB", NuGetVersion.Parse("3.0.0")), fwk45, true),
            };

            var nuGetProject = new TestNuGetProject(installedPackages);

            var target = "FixedTestA";

            // Act
            var sourceRepositoryProvider = TestSourceRepositoryUtility.CreateV2OnlySourceRepositoryProvider();
            var testSettings = new NuGet.Configuration.NullSettings();
            using (var testSolutionManager = new TestSolutionManager(true))
            {
                var deleteOnRestartManager = new TestDeleteOnRestartManager();

                var nuGetPackageManager = new NuGetPackageManager(
                    sourceRepositoryProvider,
                    testSettings,
                    testSolutionManager,
                    deleteOnRestartManager);

                var nugetProjectActions = await nuGetPackageManager.PreviewInstallPackageAsync(
                    nuGetProject,
                    target,
                    new ResolutionContext(DependencyBehavior.Lowest, false, false, VersionConstraints.None),
                    new TestNuGetProjectContext(),
                    sourceRepositoryProvider.GetRepositories().First(),
                    null,
                    CancellationToken.None);

                Assert.True(nugetProjectActions.Select(pa => pa.PackageIdentity.Id).Contains(target, StringComparer.OrdinalIgnoreCase));
            }
        }

        [Fact]
        public async Task ExecuteNuGetProjectActionsAsync_FailsIfThePackageTypeUsesManagedCodeConventions()
        {
            // Arrange
            var packageSource = new Configuration.PackageSource("some source");
            var packageSourceProvider = new TestPackageSourceProvider(new[] { packageSource });
            var sourceRepositoryProvider = new SourceRepositoryProvider(
                packageSourceProvider,
                new[] { new Lazy<INuGetResourceProvider>(() => new TestDownloadResourceProvider()) });
            var testSettings = Configuration.NullSettings.Instance;
            using (var testSolutionManager = new TestSolutionManager(true))
            {
                var deleteOnRestartManager = new TestDeleteOnRestartManager();
                var nugetProject = new TestNuGetProject(new Packaging.PackageReference[0]);

                var nuGetPackageManager = new NuGetPackageManager(
                    sourceRepositoryProvider,
                    testSettings,
                    testSolutionManager,
                    deleteOnRestartManager);
                var identity = new PackageIdentity("ManagedCodeConventions", NuGetVersion.Parse("1.0.0"));
                var actions = new[] { NuGetProjectAction.CreateInstallProjectAction(identity, sourceRepositoryProvider.CreateRepository(packageSource)) };

                // Act and Assert
                var ex = await Assert.ThrowsAsync<PackageManagement.NuGetVersionNotSatisfiedException>(() =>
                    nuGetPackageManager.ExecuteNuGetProjectActionsAsync(
                        nugetProject,
                        actions,
                        new TestNuGetProjectContext(),
                        default(CancellationToken)));
                Assert.Equal("Package 'ManagedCodeConventions 1.0.0' uses features that are not supported by the current version of NuGet. " +
                    "To upgrade NuGet, see http://docs.nuget.org/consume/installing-nuget.", ex.Message);
            }
        }

        [Fact]
        public async Task TestPacManUpdatePackagePreservePackagesConfigAttributes()
        {
            // Arrange
            var sourceRepositoryProvider = TestSourceRepositoryUtility.CreateV3OnlySourceRepositoryProvider();
            using (var testSolutionManager = new TestSolutionManager(true))
            {
                var testSettings = new Configuration.NullSettings();
                var token = CancellationToken.None;
                var deleteOnRestartManager = new TestDeleteOnRestartManager();
                var nuGetPackageManager = new NuGetPackageManager(
                    sourceRepositoryProvider,
                    testSettings,
                    testSolutionManager,
                    deleteOnRestartManager);
                var packagesFolderPath =
                    PackagesFolderPathUtility.GetPackagesFolderPath(testSolutionManager, testSettings);

                var msBuildNuGetProject = testSolutionManager.AddNewMSBuildProject();
                var packagesConfigPath = msBuildNuGetProject.PackagesConfigNuGetProject.FullPath;

                using (var writer = new StreamWriter(packagesConfigPath))
                {
                    writer.WriteLine(@"<packages>
                <package id=""NuGet.Versioning"" version=""1.0.1"" targetFramework=""net45""
                    allowedVersions=""[1.0.0, 2.0.0]"" developmentDependency=""true"" future=""abc"" />
                </packages>");
                }

                var packageIdentity = new PackageIdentity("nuget.versioning", NuGetVersion.Parse("1.0.5"));
                var packageOld = new PackageIdentity("nuget.versioning", NuGetVersion.Parse("1.0.1"));

                // Act
                await nuGetPackageManager.RestorePackageAsync(
                    packageOld,
                    new TestNuGetProjectContext(),
                    sourceRepositoryProvider.GetRepositories(),
                    token);

                var actions = await nuGetPackageManager.PreviewInstallPackageAsync(
                    msBuildNuGetProject,
                    packageIdentity,
                    new ResolutionContext(),
                    new TestNuGetProjectContext(),
                    sourceRepositoryProvider.GetRepositories().First(),
                    null,
                    token);

                await nuGetPackageManager.InstallPackageAsync(
                    msBuildNuGetProject,
                    packageIdentity,
                    new ResolutionContext(),
                    new TestNuGetProjectContext(),
                    sourceRepositoryProvider.GetRepositories(),
                    sourceRepositoryProvider.GetRepositories(),
                    token);

                var packagesInPackagesConfig = (await msBuildNuGetProject.PackagesConfigNuGetProject
                    .GetInstalledPackagesAsync(token))
                    .ToList();

                var packagesConfigXML = XDocument.Load(packagesConfigPath);
                var entry = packagesConfigXML.Element(XName.Get("packages")).Elements(XName.Get("package")).Single();

                // Assert
                Assert.Equal(2, actions.Count());
                Assert.Equal(1, packagesInPackagesConfig.Count);
                Assert.Equal(packageIdentity, packagesInPackagesConfig[0].PackageIdentity);
                Assert.Equal(msBuildNuGetProject.MSBuildNuGetProjectSystem.TargetFramework, packagesInPackagesConfig[0].TargetFramework);

                Assert.Equal("[1.0.0, 2.0.0]", entry.Attribute(XName.Get("allowedVersions")).Value);
                Assert.Equal("true", entry.Attribute(XName.Get("developmentDependency")).Value);
                Assert.Equal("abc", entry.Attribute(XName.Get("future")).Value);
            }
        }

        [Fact]
        public async Task TestPacManUpdatePackagePreservePackagesConfigAttributesMultiplePackages()
        {
            // Arrange
            var sourceRepositoryProvider = TestSourceRepositoryUtility.CreateV3OnlySourceRepositoryProvider();
            using (var testSolutionManager = new TestSolutionManager(true))
            {
                var testSettings = new Configuration.NullSettings();
                var token = CancellationToken.None;
                var deleteOnRestartManager = new TestDeleteOnRestartManager();
                var nuGetPackageManager = new NuGetPackageManager(
                    sourceRepositoryProvider,
                    testSettings,
                    testSolutionManager,
                    deleteOnRestartManager);
                var packagesFolderPath =
                    PackagesFolderPathUtility.GetPackagesFolderPath(testSolutionManager, testSettings);

                var msBuildNuGetProject = testSolutionManager.AddNewMSBuildProject();
                var packagesConfigPath = msBuildNuGetProject.PackagesConfigNuGetProject.FullPath;

                using (var writer = new StreamWriter(packagesConfigPath))
                {
                    writer.WriteLine(@"<packages>
                <package id=""NuGet.Versioning"" version=""1.0.1"" targetFramework=""net45""
                    allowedVersions=""[1.0.0, 2.0.0]"" developmentDependency=""true"" future=""abc"" />
                <package id=""newtonsoft.json"" version=""6.0.8"" targetFramework=""net45"" />
                </packages>");
                }

                var packageIdentity = new PackageIdentity("nuget.versioning", NuGetVersion.Parse("1.0.5"));
                var packageOld = new PackageIdentity("nuget.versioning", NuGetVersion.Parse("1.0.1"));

                // Act
                await nuGetPackageManager.RestorePackageAsync(
                    packageOld,
                    new TestNuGetProjectContext(),
                    sourceRepositoryProvider.GetRepositories(),
                    token);

                await nuGetPackageManager.RestorePackageAsync(
                    new PackageIdentity("newtonsoft.json", NuGetVersion.Parse("6.0.8")),
                    new TestNuGetProjectContext(),
                    sourceRepositoryProvider.GetRepositories(),
                    token);

                var actions = await nuGetPackageManager.PreviewInstallPackageAsync(
                    msBuildNuGetProject,
                    packageIdentity,
                    new ResolutionContext(),
                    new TestNuGetProjectContext(),
                    sourceRepositoryProvider.GetRepositories().First(),
                    null,
                    token);

                await nuGetPackageManager.InstallPackageAsync(
                    msBuildNuGetProject,
                    packageIdentity,
                    new ResolutionContext(),
                    new TestNuGetProjectContext(),
                    sourceRepositoryProvider.GetRepositories(),
                    sourceRepositoryProvider.GetRepositories(),
                    token);

                var packagesInPackagesConfig = (await msBuildNuGetProject.PackagesConfigNuGetProject
                    .GetInstalledPackagesAsync(token))
                    .OrderBy(package => package.PackageIdentity.Id)
                    .ToList();

                var packagesConfigXML = XDocument.Load(packagesConfigPath);
                var entry = packagesConfigXML.Element(XName.Get("packages"))
                    .Elements(XName.Get("package"))
                    .Single(package => package.Attribute(XName.Get("id")).Value
                        .Equals("nuget.versioning", StringComparison.OrdinalIgnoreCase));

                // Assert
                Assert.Equal(2, actions.Count());
                Assert.Equal(2, packagesInPackagesConfig.Count);
                Assert.Equal(packageIdentity, packagesInPackagesConfig[1].PackageIdentity);
                Assert.Equal(msBuildNuGetProject.MSBuildNuGetProjectSystem.TargetFramework, packagesInPackagesConfig[1].TargetFramework);

                Assert.Equal("[1.0.0, 2.0.0]", entry.Attribute(XName.Get("allowedVersions")).Value);
                Assert.Equal("true", entry.Attribute(XName.Get("developmentDependency")).Value);
                Assert.Equal("abc", entry.Attribute(XName.Get("future")).Value);
            }
        }

        [Fact]
        public async Task TestPacManGetLatestVersion_GatherCache()
        {
            // Arrange
            var packageIdentity = new PackageIdentity("a", new NuGetVersion(1, 0, 0));
            var bVersionRange = VersionRange.Parse("[0.5.0, 2.0.0)");
            var packages = new List<SourcePackageDependencyInfo>
            {
                new SourcePackageDependencyInfo(
                    packageIdentity.Id,
                    packageIdentity.Version,
                    new[]
                    {
                        new Packaging.Core.PackageDependency("b", bVersionRange)
                    },
                    listed: true,
                    source: null),
            };

            var resourceProviders = new List<Lazy<INuGetResourceProvider>>();
            resourceProviders.Add(new Lazy<INuGetResourceProvider>(() => new TestDependencyInfoProvider(packages)));
            resourceProviders.Add(new Lazy<INuGetResourceProvider>(() => new TestMetadataProvider(packages)));

            var packageSource = new Configuration.PackageSource("http://a");
            var packageSourceProvider = new TestPackageSourceProvider(new[] { packageSource });

            var sourceRepositoryProvider = new SourceRepositoryProvider(packageSourceProvider, resourceProviders);
            var resolutionContext = new ResolutionContext();

            // Act
            var latestVersion = await NuGetPackageManager.GetLatestVersionAsync(
                "a",
                NuGetFramework.AnyFramework,
                resolutionContext,
                sourceRepositoryProvider.GetRepositories().First(),
                Common.NullLogger.Instance,
                CancellationToken.None);

            // Assert
            var gatherCache = resolutionContext.GatherCache;
            var gatherCacheResult = gatherCache.GetPackage(packageSource, packageIdentity, NuGetFramework.AnyFramework);
            Assert.Single(gatherCacheResult.Packages);
            var packageInfo = gatherCacheResult.Packages.Single();
            Assert.Single(packageInfo.Dependencies);
            var packageDependency = packageInfo.Dependencies.Single();
            Assert.Equal("b", packageDependency.Id);
            Assert.Equal(bVersionRange.ToString(), packageDependency.VersionRange.ToString());
            Assert.True(packageDependency.VersionRange.IncludePrerelease);
        }

        private class TestDownloadResourceProvider : ResourceProvider
        {
            public TestDownloadResourceProvider()
                : base(typeof(DownloadResource))
            {
            }

            public override Task<Tuple<bool, INuGetResource>> TryCreate(SourceRepository source, CancellationToken token)
            {
                INuGetResource resource = new TestDownloadResource();
                return Task.FromResult(Tuple.Create(true, resource));
            }
        }

        private class TestDownloadResource : DownloadResource
        {
            public override Task<DownloadResourceResult> GetDownloadResourceResultAsync(
                PackageIdentity identity,
                Configuration.ISettings settings,
                NuGet.Common.ILogger logger,
                CancellationToken token)
            {
                var packageReader = new Mock<PackageReaderBase>(
                    new FrameworkNameProvider(new[] { DefaultFrameworkMappings.Instance },
                    new[] { DefaultPortableFrameworkMappings.Instance }))
                {
                    CallBase = true
                };

                packageReader
                    .Setup(p => p.GetIdentity())
                    .Returns(new PackageIdentity("ManagedCodeConventions", NuGetVersion.Parse("1.0.0")));
                packageReader
                    .Setup(p => p.GetMinClientVersion())
                    .Returns(new NuGetVersion(2, 0, 0));
                packageReader
                    .Setup(p => p.GetPackageType())
                    .Returns(new PackageType("Managed", new Version(2, 0)));

                return Task.FromResult(new DownloadResourceResult(Stream.Null, packageReader.Object));
            }
        }

        private SourceRepositoryProvider CreateSource(List<SourcePackageDependencyInfo> packages)
        {
            var resourceProviders = new List<Lazy<INuGetResourceProvider>>();
            resourceProviders.Add(new Lazy<INuGetResourceProvider>(() => new TestDependencyInfoProvider(packages)));
            resourceProviders.Add(new Lazy<INuGetResourceProvider>(() => new TestMetadataProvider(packages)));

            var packageSource = new Configuration.PackageSource("http://temp");
            var packageSourceProvider = new TestPackageSourceProvider(new[] { packageSource });

            return new SourceRepositoryProvider(packageSourceProvider, resourceProviders);
        }

        private static void Expected(List<Tuple<PackageIdentity, NuGetProjectActionType>> expected, string id, NuGetVersion oldVersion, NuGetVersion newVersion)
        {
            expected.Add(Tuple.Create(new PackageIdentity(id, oldVersion), NuGetProjectActionType.Uninstall));
            expected.Add(Tuple.Create(new PackageIdentity(id, newVersion), NuGetProjectActionType.Install));
        }

        private static void Expected(List<Tuple<PackageIdentity, NuGetProjectActionType>> expected, string id, NuGetVersion newVersion)
        {
            expected.Add(Tuple.Create(new PackageIdentity(id, newVersion), NuGetProjectActionType.Install));
        }

        private static bool Compare(
            IEnumerable<Tuple<PackageIdentity, NuGetProjectActionType>> lhs,
            IEnumerable<Tuple<PackageIdentity, NuGetProjectActionType>> rhs)
        {
            bool ok = true;
            ok &= RhsContainsAllLhs(lhs, rhs);
            ok &= RhsContainsAllLhs(rhs, lhs);
            return ok;
        }

        private static bool RhsContainsAllLhs(
            IEnumerable<Tuple<PackageIdentity, NuGetProjectActionType>> lhs,
            IEnumerable<Tuple<PackageIdentity, NuGetProjectActionType>> rhs)
        {
            foreach (var item in lhs)
            {
                if (!rhs.Contains(item, new ActionComparer()))
                {
                    return false;
                }
            }
            return true;
        }

        private class ActionComparer : IEqualityComparer<Tuple<PackageIdentity, NuGetProjectActionType>>
        {
            public bool Equals(Tuple<PackageIdentity, NuGetProjectActionType> x, Tuple<PackageIdentity, NuGetProjectActionType> y)
            {
                bool f1 = x.Item1.Equals(y.Item1);
                bool f2 = x.Item2 == y.Item2;
                return f1 && f2;
            }

            public int GetHashCode(Tuple<PackageIdentity, NuGetProjectActionType> obj)
            {
                return obj.GetHashCode();
            }
        }

        private static Packaging.IPackageFile CreatePackageFile(string name)
        {
            var file = new Mock<Packaging.IPackageFile>();
            file.SetupGet(f => f.Path).Returns(name);
            file.Setup(f => f.GetStream()).Returns(new MemoryStream());

            string effectivePath;
            var fx = FrameworkNameUtility.ParseFrameworkNameFromFilePath(name, out effectivePath);
            file.SetupGet(f => f.EffectivePath).Returns(effectivePath);
            file.SetupGet(f => f.TargetFramework).Returns(fx);

            return file.Object;
        }
    }
}
