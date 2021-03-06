﻿using System;
using System.Linq;
using System.Threading.Tasks;
using Wabbajack.Common;
using Wabbajack.Lib;
using Wabbajack.Lib.AuthorApi;
using Wabbajack.Lib.Downloaders;
using Wabbajack.Server.DataLayer;
using Xunit;
using Xunit.Abstractions;

namespace Wabbajack.BuildServer.Test
{
    public class AuthoredFilesTests : ABuildServerSystemTest
    {
        public AuthoredFilesTests(ITestOutputHelper output, SingletonAdaptor<BuildServerFixture> fixture) : base(output, fixture)
        {
        }

        [Fact]
        public async Task CanUploadDownloadAndDeleteAuthoredFiles()
        {
            using var file = new TempFile();
            await file.Path.WriteAllBytesAsync(RandomData(Consts.UPLOADED_FILE_BLOCK_SIZE * 4 + Consts.UPLOADED_FILE_BLOCK_SIZE / 3));
            var originalHash = await file.Path.FileHashAsync();

            var client = await Client.Create(Fixture.APIKey);
            using var queue = new WorkQueue(2);
            var uri = await client.UploadFile(queue, file.Path, (s, percent) => Utils.Log($"({percent}) {s}"));

            var data = await Fixture.GetService<SqlService>().AllAuthoredFiles();
            Assert.Contains((string)file.Path.FileName, data.Select(f => f.OriginalFileName));

            var result = await _client.GetStringAsync(MakeURL("authored_files"));
            Assert.Contains((string)file.Path.FileName, result);

            var state = await DownloadDispatcher.Infer(uri);
            Assert.IsType<WabbajackCDNDownloader.State>(state);

            await state.Download(new Archive(state) {Name = (string)file.Path.FileName}, file.Path);
            Assert.Equal(originalHash, await file.Path.FileHashAsync());

        }

    }
}
