﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using Wabbajack.Common;
using Wabbajack.DTOs;
using Wabbajack.Messages;
using Wabbajack.Models;
using Wabbajack.Networking.WabbajackClientApi;
using Wabbajack.Paths;
using Wabbajack.RateLimiter;
using Wabbajack.Services.OSIntegrated.Services;

namespace Wabbajack;


public struct ModListTag
{
    public ModListTag(string name)
    {
        Name = name;
    }

    public string Name { get; }
}

public class BaseModListMetadataVM : ViewModel
{
    public ModlistMetadata Metadata { get; }

    public AbsolutePath Location { get; }

    public LoadingLock LoadingImageLock { get; } = new();

    [Reactive]
    public List<ModListTag> ModListTagList { get; protected set; }

    [Reactive]
    public Percent ProgressPercent { get; protected set; }

    [Reactive]
    public bool IsBroken { get; protected set; }
    
    [Reactive]
    public ModListStatus Status { get; set; }
    
    [Reactive]
    public bool IsDownloading { get; protected set; }

    [Reactive]
    public string DownloadSizeText { get; protected set; }

    [Reactive]
    public string InstallSizeText { get; protected set; }
    
    [Reactive]
    public string TotalSizeRequirementText { get; protected set; }
    
    [Reactive]
    public string VersionText { get; protected set; }

    [Reactive]
    public bool ImageContainsTitle { get; protected set; }

    [Reactive]
    public GameMetaData GameMetaData { get; protected set; }

    [Reactive]

    public bool DisplayVersionOnlyInInstallerView { get; protected set; }

    [Reactive]
    public IErrorResponse Error { get; protected set; }

    protected readonly ObservableAsPropertyHelper<BitmapImage> _Image;
    public BitmapImage Image => _Image.Value;

    protected readonly ObservableAsPropertyHelper<bool> _LoadingImage;
    public bool LoadingImage => _LoadingImage.Value;

    protected Subject<bool> IsLoadingIdle;
    protected readonly ILogger _logger;
    protected readonly ModListDownloadMaintainer _maintainer;
    protected readonly Client _wjClient;
    protected readonly CancellationToken _cancellationToken;

    public BaseModListMetadataVM(ILogger logger, ModlistMetadata metadata,
        ModListDownloadMaintainer maintainer, Client wjClient, CancellationToken cancellationToken)
    {
        _logger = logger;
        _maintainer = maintainer;
        Metadata = metadata;
        _wjClient = wjClient;
        _cancellationToken = cancellationToken;

        GameMetaData = Metadata.Game.MetaData();
        Location = LauncherUpdater.CommonFolder.Value.Combine("downloaded_mod_lists", Metadata.NamespacedName).WithExtension(Ext.Wabbajack);
        ModListTagList = new List<ModListTag>();
        
        UpdateStatus().FireAndForget();

        Metadata.Tags.ForEach(tag =>
        {
            ModListTagList.Add(new ModListTag(tag));
        });
        ModListTagList.Add(new ModListTag(GameMetaData.HumanFriendlyGameName));

        DownloadSizeText = "Download size: " + UIUtils.FormatBytes(Metadata.DownloadMetadata.SizeOfArchives);
        InstallSizeText = "Installation size: " + UIUtils.FormatBytes(Metadata.DownloadMetadata.SizeOfInstalledFiles);
        TotalSizeRequirementText =  "Total size requirement: " + UIUtils.FormatBytes( Metadata.DownloadMetadata.TotalSize );
        VersionText = "Modlist version: " + Metadata.Version;
        ImageContainsTitle = Metadata.ImageContainsTitle;
        DisplayVersionOnlyInInstallerView = Metadata.DisplayVersionOnlyInInstallerView;
        IsBroken = metadata.ValidationSummary.HasFailures || metadata.ForceDown;

        IsLoadingIdle = new Subject<bool>();
        
        var modlistImageSource = Metadata.ValidationSummary?.SmallImage?.ToString() ?? Metadata.Links.ImageUri;
        var imageObs = Observable.Return(modlistImageSource)
            .DownloadBitmapImage((ex) => _logger.LogError("Error downloading modlist image {Title} from {ImageUri}: {Exception}", Metadata.Title, modlistImageSource, ex.Message), LoadingImageLock);

        _Image = imageObs
            .ToGuiProperty(this, nameof(Image));

        _LoadingImage = imageObs
            .Select(x => false)
            .StartWith(true)
            .ToGuiProperty(this, nameof(LoadingImage));
    }

    protected async Task Download()
    {
        try
        {
            Status = ModListStatus.Downloading;

            using var ll = LoadingLock.WithLoading();
            var (progress, task) = _maintainer.DownloadModlist(Metadata, _cancellationToken);
            var dispose = progress
                .BindToStrict(this, vm => vm.ProgressPercent);
            try
            {
                await _wjClient.SendMetric("downloading", Metadata.Title);
                await task;
                await UpdateStatus();
            }
            finally
            {
                dispose.Dispose();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "While downloading {Modlist}", Metadata.RepositoryName);
            await UpdateStatus();
        }
    }

    protected async Task UpdateStatus()
    {
        if (await _maintainer.HaveModList(Metadata))
            Status = ModListStatus.Downloaded;
        else if (LoadingLock.IsLoading)
            Status = ModListStatus.Downloading;
        else
            Status = ModListStatus.NotDownloaded;
    }

    public enum ModListStatus
    {
        NotDownloaded,
        Downloading,
        Downloaded
    }
}
