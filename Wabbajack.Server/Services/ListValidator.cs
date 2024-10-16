﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Org.BouncyCastle.Crypto.Digests;
using RocksDbSharp;
using Wabbajack.BuildServer;
using Wabbajack.Common;
using Wabbajack.Lib;
using Wabbajack.Lib.Downloaders;
using Wabbajack.Lib.ModListRegistry;
using Wabbajack.Lib.NexusApi;
using Wabbajack.Server.DataLayer;
using Wabbajack.Server.DTOs;

namespace Wabbajack.Server.Services
{
    public class ListValidator : AbstractService<ListValidator, int>
    {
        private SqlService _sql;
        private DiscordWebHook _discord;
        private NexusKeyMaintainance _nexus;

        public IEnumerable<(ModListSummary Summary, DetailedStatus Detailed)> Summaries { get; private set; } =
            new (ModListSummary Summary, DetailedStatus Detailed)[0];


        public ListValidator(ILogger<ListValidator> logger, AppSettings settings, SqlService sql, DiscordWebHook discord, NexusKeyMaintainance nexus) 
            : base(logger, settings, TimeSpan.FromMinutes(10))
        {
            _sql = sql;
            _discord = discord;
            _nexus = nexus;
        }

        public override async Task<int> Execute()
        {
            var data = await _sql.GetValidationData();

            using var queue = new WorkQueue();

            var results = await data.ModLists.PMap(queue, async list =>
            {
                var (metadata, modList) = list;
                var archives = await modList.Archives.PMap(queue, async archive =>
                {
                    var (_, result) = await ValidateArchive(data, archive);
                    // TODO : auto-healing goes here
                    return (archive, result);
                });

                var failedCount = archives.Count(f => f.Item2 == ArchiveStatus.InValid);
                var passCount = archives.Count(f => f.Item2 == ArchiveStatus.Valid || f.Item2 == ArchiveStatus.Updated);
                var updatingCount = archives.Count(f => f.Item2 == ArchiveStatus.Updating);

                var summary =  new ModListSummary
                {
                    Checked = DateTime.UtcNow,
                    Failed = failedCount,
                    Passed = passCount,
                    Updating = updatingCount,
                    MachineURL = metadata.Links.MachineURL,
                    Name = metadata.Title,
                };

                var detailed = new DetailedStatus
                {
                    Name = metadata.Title,
                    Checked = DateTime.UtcNow,
                    DownloadMetaData = metadata.DownloadMetadata,
                    HasFailures = failedCount > 0,
                    MachineName = metadata.Links.MachineURL,
                    Archives = archives.Select(a => new DetailedStatusItem
                    {
                        Archive = a.Item1, IsFailing = a.Item2 == ArchiveStatus.InValid || a.Item2 == ArchiveStatus.Updating
                    }).ToList()
                };

                return (summary, detailed);
            });
            Summaries = results;
            return Summaries.Count(s => s.Summary.HasFailures);
        }
        
        private async Task<(Archive archive, ArchiveStatus)> ValidateArchive(ValidationData data, Archive archive)
        {
            switch (archive.State)
            {
                case GoogleDriveDownloader.State _:
                    // Disabled for now due to GDrive rate-limiting the build server
                    return (archive, ArchiveStatus.Valid);
                case NexusDownloader.State nexusState when data.NexusFiles.Contains((
                    nexusState.Game.MetaData().NexusGameId, nexusState.ModID, nexusState.FileID)):
                    return (archive, ArchiveStatus.Valid);
                case NexusDownloader.State ns:
                    return (archive, await FastNexusModStats(ns));
                case ManualDownloader.State _:
                    return (archive, ArchiveStatus.Valid);
                default:
                {
                    if (data.ArchiveStatus.TryGetValue((archive.State.PrimaryKeyString, archive.Hash),
                        out bool isValid))
                    {
                        return isValid ? (archive, ArchiveStatus.Valid) : (archive, ArchiveStatus.InValid);
                    }

                    return (archive, ArchiveStatus.InValid);
                }
            }
        }
        
        private AsyncLock _lock = new AsyncLock();

        public async Task<ArchiveStatus> FastNexusModStats(NexusDownloader.State ns)
        {
            // Check if some other thread has added them
            var mod = await _sql.GetNexusModInfoString(ns.Game, ns.ModID);
            var files = await _sql.GetModFiles(ns.Game, ns.ModID);

            if (mod == null || files == null)
            {
                // Aquire the lock
                using var lck = await _lock.WaitAsync();
                
                // Check again
                mod = await _sql.GetNexusModInfoString(ns.Game, ns.ModID);
                files = await _sql.GetModFiles(ns.Game, ns.ModID);

                if (mod == null || files == null)
                {

                    NexusApiClient nexusClient = await _nexus.GetClient();
                    var queryTime = DateTime.UtcNow;

                    try
                    {
                        if (mod == null)
                        {
                            _logger.Log(LogLevel.Information, $"Found missing Nexus mod info {ns.Game} {ns.ModID}");
                            try
                            {
                                mod = await nexusClient.GetModInfo(ns.Game, ns.ModID, false);
                            }
                            catch
                            {
                                mod = new ModInfo
                                {
                                    mod_id = ns.ModID.ToString(),
                                    game_id = ns.Game.MetaData().NexusGameId,
                                    available = false
                                };
                            }

                            try
                            {
                                await _sql.AddNexusModInfo(ns.Game, ns.ModID, queryTime, mod);
                            }
                            catch (Exception _)
                            {
                                // Could be a PK constraint failure
                            }

                        }

                        if (files == null)
                        {
                            _logger.Log(LogLevel.Information, $"Found missing Nexus mod info {ns.Game} {ns.ModID}");
                            try
                            {
                                files = await nexusClient.GetModFiles(ns.Game, ns.ModID, false);
                            }
                            catch
                            {
                                files = new NexusApiClient.GetModFilesResponse {files = new List<NexusFileInfo>()};
                            }

                            try
                            {
                                await _sql.AddNexusModFiles(ns.Game, ns.ModID, queryTime, files);
                            }
                            catch (Exception _)
                            {
                                // Could be a PK constraint failure
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        return ArchiveStatus.InValid;
                    }
                }
            }

            if (mod.available && files.files.Any(f => !string.IsNullOrEmpty(f.category_name) && f.file_id == ns.FileID))
                return ArchiveStatus.Valid;
            return ArchiveStatus.InValid;

        }
    }
}
