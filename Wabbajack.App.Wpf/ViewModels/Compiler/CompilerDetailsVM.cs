﻿using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reactive;
using Microsoft.Extensions.Logging;
using Wabbajack.Messages;
using ReactiveUI;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using DynamicData;
using Microsoft.WindowsAPICodePack.Dialogs;
using ReactiveUI.Fody.Helpers;
using Wabbajack.Common;
using Wabbajack.Compiler;
using Wabbajack.DTOs;
using Wabbajack.DTOs.JsonConverters;
using Wabbajack.Extensions;
using Wabbajack.Installer;
using Wabbajack.Models;
using Wabbajack.Networking.WabbajackClientApi;
using Wabbajack.Paths;
using Wabbajack.Paths.IO;
using Wabbajack.RateLimiter;
using Wabbajack.Services.OSIntegrated;

namespace Wabbajack
{
    public enum CompilerState
    {
        Configuration,
        Compiling,
        Completed,
        Errored
    }
    public class CompilerDetailsVM : BaseCompilerVM, ICpuStatusVM
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ResourceMonitor _resourceMonitor;
        private readonly CompilerSettingsInferencer _inferencer;
        
        public CompilerFileManagerVM CompilerFileManagerVM { get; private set; }
        [Reactive] public string StatusText { get; set; }
        [Reactive] public Percent StatusProgress { get; set; }

        [Reactive]
        public CompilerState State { get; set; }
        
        [Reactive]
        public MO2CompilerVM SubCompilerVM { get; set; }
        
        // Paths 
        public FilePickerVM ModlistLocation { get; private set; }
        public FilePickerVM DownloadLocation { get; private set; }
        public FilePickerVM OutputLocation { get; private set; }

        public FilePickerVM ModListImageLocation { get; private set; } = new();
        
        /* public ReactiveCommand<Unit, Unit> ExecuteCommand { get; } */
        public ReactiveCommand<Unit, Unit> ReInferSettingsCommand { get; set; }
        public ReactiveCommand<Unit, Unit> NextCommand { get; }

        public LogStream LoggerProvider { get; }
        public ReadOnlyObservableCollection<CPUDisplayVM> StatusList => _resourceMonitor.Tasks;
        
        [Reactive]
        public ErrorResponse ErrorState { get; private set; }
        
        public CompilerDetailsVM(ILogger<CompilerDetailsVM> logger, DTOSerializer dtos, SettingsManager settingsManager,
            IServiceProvider serviceProvider, LogStream loggerProvider, ResourceMonitor resourceMonitor, 
            CompilerSettingsInferencer inferencer, Client wjClient, CompilerFileManagerVM compilerFileManagerVM) : base(dtos, settingsManager, logger, wjClient)
        {
            _serviceProvider = serviceProvider;
            LoggerProvider = loggerProvider;
            _resourceMonitor = resourceMonitor;
            _inferencer = inferencer;
            CompilerFileManagerVM = compilerFileManagerVM;

            StatusText = "Compiler Settings";
            StatusProgress = Percent.Zero;

            BackCommand = ReactiveCommand.CreateFromTask(async () =>
            {
                await SaveSettings();
                NavigateToGlobal.Send(ScreenType.Home);
            });

            SubCompilerVM = new MO2CompilerVM(this);

            NextCommand = ReactiveCommand.CreateFromTask(NextPage);

            
            this.WhenActivated(disposables =>
            {
                State = CompilerState.Configuration;

                ModlistLocation = new FilePickerVM
                {
                    ExistCheckOption = FilePickerVM.CheckOptions.On,
                    PathType = FilePickerVM.PathTypeOptions.File,
                    PromptTitle = "Select a config file or a modlist.txt file",
                    TargetPath = Settings.ProfilePath
                };

                ModlistLocation.Filters.AddRange(new[]
                {
                    new CommonFileDialogFilter("MO2 Modlist", "*" + Ext.Txt),
                    new CommonFileDialogFilter("Compiler Settings File", "*" + Ext.CompilerSettings)
                });

                DownloadLocation = new FilePickerVM
                {
                    ExistCheckOption = FilePickerVM.CheckOptions.On,
                    PathType = FilePickerVM.PathTypeOptions.Folder,
                    PromptTitle = "Location where the downloads for this list are stored"
                };

                OutputLocation = new FilePickerVM
                {
                    ExistCheckOption = FilePickerVM.CheckOptions.Off,
                    PathType = FilePickerVM.PathTypeOptions.Folder,
                    PromptTitle = "Location where the compiled modlist will be stored"
                };


                ModlistLocation.WhenAnyValue(vm => vm.TargetPath)
                    .Subscribe(async p => {
                        if (p == default) return;
                        if (Settings.CompilerSettingsPath != default) return;
                        else if(p.FileName == "modlist.txt".ToRelativePath()) await ReInferSettings(p);
                    })
                    .DisposeWith(disposables);

                this.WhenAnyValue(x => x.DownloadLocation.TargetPath)
                    .CombineLatest(this.WhenAnyValue(x => x.ModlistLocation.TargetPath),
                        this.WhenAnyValue(x => x.OutputLocation.TargetPath),
                        this.WhenAnyValue(x => x.DownloadLocation.ErrorState),
                        this.WhenAnyValue(x => x.ModlistLocation.ErrorState),
                        this.WhenAnyValue(x => x.OutputLocation.ErrorState))
                    .Select(_ => Validate())
                    .BindToStrict(this, vm => vm.ErrorState)
                    .DisposeWith(disposables);

                /*

                ModListImageLocation.WhenAnyValue(x => x.TargetPath)
                                    .BindToStrict(this, vm => vm.Settings.ModListImage)
                                    .DisposeWith(disposables);

                DownloadLocation.WhenAnyValue(x => x.TargetPath)
                                .BindToStrict(this, vm => vm.Settings.Downloads)
                                .DisposeWith(disposables);

                Settings.WhenAnyValue(x => x.Downloads)
                        .BindToStrict(this, vm => vm.DownloadLocation.TargetPath)
                        .DisposeWith(disposables);
                */

            });
        }

        private async Task ReInferSettings(AbsolutePath filePath)
        {
            var newSettings = await _inferencer.InferModListFromLocation(filePath);

            if (newSettings == null)
            {
                _logger.LogError("Cannot infer settings from {0}", filePath);
                return;
            }

            Settings.Source = newSettings.Source;
            Settings.Downloads = newSettings.Downloads;

            if (string.IsNullOrEmpty(Settings.ModListName))
                Settings.OutputFile = newSettings.OutputFile.Combine(newSettings.Profile).WithExtension(Ext.Wabbajack);
            else
                Settings.OutputFile = newSettings.OutputFile.Combine(newSettings.ModListName).WithExtension(Ext.Wabbajack);

            Settings.Game = newSettings.Game;
            Settings.Include = newSettings.Include.ToHashSet();
            Settings.Ignore = newSettings.Ignore.ToHashSet();
            Settings.AlwaysEnabled = newSettings.AlwaysEnabled.ToHashSet();
            Settings.NoMatchInclude = newSettings.NoMatchInclude.ToHashSet();
            Settings.AdditionalProfiles = newSettings.AdditionalProfiles;
        }

        private ErrorResponse Validate()
        {
            var errors = new List<ErrorResponse>
            {
                DownloadLocation.ErrorState,
                ModlistLocation.ErrorState,
                OutputLocation.ErrorState
            };
            return ErrorResponse.Combine(errors);
        }

        private async Task<CompilerSettings> InferModListFromLocation(AbsolutePath path)
        {
            using var _ = LoadingLock.WithLoading();

            CompilerSettings settings;
            if (path == default) return new();
            if (path.FileName.Extension == Ext.CompilerSettings)
            {
                await using var fs = path.Open(FileMode.Open, FileAccess.Read, FileShare.Read);
                settings = (await _dtos.DeserializeAsync<CompilerSettings>(fs))!;
            }
            else if (path.FileName == "modlist.txt".ToRelativePath())
            {
                settings = await _inferencer.InferModListFromLocation(path);
                if (settings == null) return new();
            }
            else
            {
                return new();
            }

            return settings;
        }

        private async Task NextPage()
        {
            await SaveSettings();
            NavigateToGlobal.Send(ScreenType.CompilerFileManager);
            LoadCompilerSettings.Send(Settings.ToCompilerSettings());
        }

        #region ListOps

        public void AddOtherProfile(string profile)
        {
            Settings.AdditionalProfiles = (Settings.AdditionalProfiles ?? Array.Empty<string>()).Append(profile).Distinct().ToArray();
        }

        public void RemoveProfile(string profile)
        {
            Settings.AdditionalProfiles = Settings.AdditionalProfiles.Where(p => p != profile).ToArray();
        }
        
        public void AddAlwaysEnabled(RelativePath path)
        {
            Settings.AlwaysEnabled = (Settings.AlwaysEnabled ?? new()).Append(path).Distinct().ToHashSet();
        }

        public void RemoveAlwaysEnabled(RelativePath path)
        {
            Settings.AlwaysEnabled = Settings.AlwaysEnabled.Where(p => p != path).ToHashSet();
        }
        
        public void AddNoMatchInclude(RelativePath path)
        {
            Settings.NoMatchInclude = (Settings.NoMatchInclude ?? new()).Append(path).Distinct().ToHashSet();
        }

        public void RemoveNoMatchInclude(RelativePath path)
        {
            Settings.NoMatchInclude = Settings.NoMatchInclude.Where(p => p != path).ToHashSet();
        }
        
        public void AddInclude(RelativePath path)
        {
            Settings.Include = (Settings.Include ?? new()).Append(path).Distinct().ToHashSet();
        }

        public void RemoveInclude(RelativePath path)
        {
            Settings.Include = Settings.Include.Where(p => p != path).ToHashSet();
        }

        
        public void AddIgnore(RelativePath path)
        {
            Settings.Ignore = (Settings.Ignore ?? new()).Append(path).Distinct().ToHashSet();
        }

        public void RemoveIgnore(RelativePath path)
        {
            Settings.Ignore = Settings.Ignore.Where(p => p != path).ToHashSet();
        }

        #endregion
    }
}
