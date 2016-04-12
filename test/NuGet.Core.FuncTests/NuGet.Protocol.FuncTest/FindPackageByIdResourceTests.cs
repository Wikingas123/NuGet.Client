﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using NuGet.Protocol.Core.v3;
using NuGet.Protocol.Core.Types;

namespace NuGet.Protocol.FuncTest
{
    public class FindPackageByIdResourceTests
    {
        [Theory]
        [InlineData(@"http://nexusservertest:8081/nexus/service/local/nuget/NuGet/")]
        [InlineData(@"http://progetserver:8081/nuget/nuget")]
        [InlineData(@"http://klondikeserver:8081/api/odata/")]
        [InlineData(@"http://artifactory:8081/artifactory/api/nuget/nuget")]
        [InlineData(@"http://nugetserverendpoint.azurewebsites.net/nuget")]
        [InlineData(@"https://www.myget.org/F/myget-server-test/api/v2")]
        public async Task FindPackageByIdResource_NormalizedVersion(string packageSource)
        {
            // Arrange
            var repo = Repository.Factory.GetCoreV3(packageSource);
            var findPackageByIdResource = await repo.GetResourceAsync<FindPackageByIdResource>();
            var context = new SourceCacheContext();
            context.NoCache = true;
            findPackageByIdResource.CacheContext = context;

            // Act
            var packages = await findPackageByIdResource.GetAllVersionsAsync("owin", CancellationToken.None);

            // Assert
            Assert.Equal(1, packages.Count());
            Assert.Equal("1.0", packages.FirstOrDefault().ToString());
        }

        [Theory]
        [InlineData(@"http://nexusservertest:8081/nexus/service/local/nuget/NuGet/")]
        [InlineData(@"http://progetserver:8081/nuget/nuget")]
        [InlineData(@"http://klondikeserver:8081/api/odata/")]
        [InlineData(@"http://artifactory:8081/artifactory/api/nuget/nuget")]
        [InlineData(@"http://nugetserverendpoint.azurewebsites.net/nuget")]
        [InlineData(@"https://www.myget.org/F/myget-server-test/api/v2")]
        public async Task FindPackageByIdResource_NoDependencyVersion(string packageSource)
        {
            // Arrange
            var repo = Repository.Factory.GetCoreV3(packageSource);
            var findPackageByIdResource = await repo.GetResourceAsync<FindPackageByIdResource>();
            var context = new SourceCacheContext();
            context.NoCache = true;
            findPackageByIdResource.CacheContext = context;

            // Act
            var packages = await findPackageByIdResource.GetAllVersionsAsync("costura.fody", CancellationToken.None);

            // Assert
            Assert.Equal(1, packages.Count());
            Assert.Equal("1.3.3.0", packages.FirstOrDefault().ToString());
        }

        [Theory]
        [InlineData(@"http://nexusservertest:8081/nexus/service/local/nuget/NuGet/")]
        [InlineData(@"http://progetserver:8081/nuget/nuget")]
        [InlineData(@"http://klondikeserver:8081/api/odata/")]
        [InlineData(@"http://artifactory:8081/artifactory/api/nuget/nuget")]
        [InlineData(@"http://nugetserverendpoint.azurewebsites.net/nuget")]
        [InlineData(@"https://www.myget.org/F/myget-server-test/api/v2")]
        public async Task FindPackageByIdResource_Basic(string packageSource)
        {
            // Arrange
            var repo = Repository.Factory.GetCoreV3(packageSource);
            var findPackageByIdResource = await repo.GetResourceAsync<FindPackageByIdResource>();
            var context = new SourceCacheContext();
            context.NoCache = true;
            findPackageByIdResource.CacheContext = context;

            // Act
            var packages = await findPackageByIdResource.GetAllVersionsAsync("Newtonsoft.json", CancellationToken.None);

            // Assert
            Assert.Equal(1, packages.Count());
            Assert.Equal("8.0.3", packages.FirstOrDefault().ToString());
        }
    }
}
