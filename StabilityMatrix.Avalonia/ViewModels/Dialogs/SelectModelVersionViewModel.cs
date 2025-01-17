﻿using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls.Notifications;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using FluentAvalonia.UI.Controls;
using StabilityMatrix.Avalonia.Controls;
using StabilityMatrix.Avalonia.Languages;
using StabilityMatrix.Avalonia.Models;
using StabilityMatrix.Avalonia.Services;
using StabilityMatrix.Avalonia.ViewModels.Base;
using StabilityMatrix.Core.Attributes;
using StabilityMatrix.Core.Helper;
using StabilityMatrix.Core.Models.FileInterfaces;
using StabilityMatrix.Core.Services;

namespace StabilityMatrix.Avalonia.ViewModels.Dialogs;

[ManagedService]
[Transient]
public partial class SelectModelVersionViewModel : ContentDialogViewModelBase
{
    private readonly ISettingsManager settingsManager;
    private readonly IDownloadService downloadService;
    private readonly IModelIndexService modelIndexService;
    private readonly INotificationService notificationService;

    public required ContentDialog Dialog { get; set; }
    public required IReadOnlyList<ModelVersionViewModel> Versions { get; set; }
    public required string Description { get; set; }
    public required string Title { get; set; }

    [ObservableProperty]
    private Bitmap? previewImage;

    [ObservableProperty]
    private ModelVersionViewModel? selectedVersionViewModel;

    [ObservableProperty]
    private CivitFileViewModel? selectedFile;

    [ObservableProperty]
    private bool isImportEnabled;

    [ObservableProperty]
    private ObservableCollection<ImageSource> imageUrls = new();

    [ObservableProperty]
    private bool canGoToNextImage;

    [ObservableProperty]
    private bool canGoToPreviousImage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayedPageNumber))]
    private int selectedImageIndex;

    [ObservableProperty]
    private string importTooltip = string.Empty;

    public int DisplayedPageNumber => SelectedImageIndex + 1;

    public SelectModelVersionViewModel(
        ISettingsManager settingsManager,
        IDownloadService downloadService,
        IModelIndexService modelIndexService,
        INotificationService notificationService
    )
    {
        this.settingsManager = settingsManager;
        this.downloadService = downloadService;
        this.modelIndexService = modelIndexService;
        this.notificationService = notificationService;
    }

    public override void OnLoaded()
    {
        SelectedVersionViewModel = Versions[0];
        CanGoToNextImage = true;
    }

    partial void OnSelectedVersionViewModelChanged(ModelVersionViewModel? value)
    {
        var nsfwEnabled = settingsManager.Settings.ModelBrowserNsfwEnabled;
        var allImages = value
            ?.ModelVersion
            ?.Images
            ?.Where(img => nsfwEnabled || img.Nsfw == "None")
            ?.Select(x => new ImageSource(x.Url))
            .ToList();

        if (allImages == null || !allImages.Any())
        {
            allImages = new List<ImageSource> { new(Assets.NoImage) };
            CanGoToNextImage = false;
        }
        else
        {
            CanGoToNextImage = allImages.Count > 1;
        }

        Dispatcher.UIThread.Post(() =>
        {
            CanGoToPreviousImage = false;
            SelectedFile = SelectedVersionViewModel?.CivitFileViewModels.FirstOrDefault();
            ImageUrls = new ObservableCollection<ImageSource>(allImages);
            SelectedImageIndex = 0;
        });
    }

    partial void OnSelectedFileChanged(CivitFileViewModel? value)
    {
        if (value is { IsInstalled: true }) { }

        var canImport = true;
        if (settingsManager.IsLibraryDirSet)
        {
            var fileSizeBytes = value?.CivitFile.SizeKb * 1024;
            var freeSizeBytes =
                SystemInfo.GetDiskFreeSpaceBytes(settingsManager.ModelsDirectory) ?? long.MaxValue;
            canImport = fileSizeBytes < freeSizeBytes;
            ImportTooltip = canImport
                ? "Free space after download: "
                    + (
                        freeSizeBytes < long.MaxValue
                            ? Size.FormatBytes(Convert.ToUInt64(freeSizeBytes - fileSizeBytes))
                            : "Unknown"
                    )
                : $"Not enough space on disk. Need {Size.FormatBytes(Convert.ToUInt64(fileSizeBytes))} but only have {Size.FormatBytes(Convert.ToUInt64(freeSizeBytes))}";
        }
        else
        {
            ImportTooltip = "Please set the library directory in settings";
        }

        IsImportEnabled = value?.CivitFile != null && canImport;
    }

    public void Cancel()
    {
        Dialog.Hide(ContentDialogResult.Secondary);
    }

    public void Import()
    {
        Dialog.Hide(ContentDialogResult.Primary);
    }

    public async Task Delete()
    {
        if (SelectedFile == null)
            return;

        var fileToDelete = SelectedFile;
        var originalSelectedVersionVm = SelectedVersionViewModel;

        var hash = fileToDelete.CivitFile.Hashes.BLAKE3;
        if (string.IsNullOrWhiteSpace(hash))
        {
            notificationService.Show(
                "Error deleting file",
                "Could not delete model, hash is missing.",
                NotificationType.Error
            );
            return;
        }

        var matchingModels = (await modelIndexService.FindByHashAsync(hash)).ToList();

        if (matchingModels.Count == 0)
        {
            await modelIndexService.RefreshIndex();
            matchingModels = (await modelIndexService.FindByHashAsync(hash)).ToList();

            if (matchingModels.Count == 0)
            {
                notificationService.Show(
                    "Error deleting file",
                    "Could not delete model, model not found in index.",
                    NotificationType.Error
                );
                return;
            }
        }

        var dialog = new BetterContentDialog
        {
            Title = Resources.Label_AreYouSure,
            MaxDialogWidth = 750,
            MaxDialogHeight = 850,
            PrimaryButtonText = Resources.Action_Yes,
            IsPrimaryButtonEnabled = true,
            IsSecondaryButtonEnabled = false,
            CloseButtonText = Resources.Action_Cancel,
            DefaultButton = ContentDialogButton.Close,
            Content =
                $"The following files:\n{string.Join('\n', matchingModels.Select(x => $"- {x.FileName}"))}\n"
                + "and all associated metadata files will be deleted. Are you sure?",
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            foreach (var localModel in matchingModels)
            {
                var checkpointPath = new FilePath(localModel.GetFullPath(settingsManager.ModelsDirectory));
                if (File.Exists(checkpointPath))
                {
                    File.Delete(checkpointPath);
                }

                var previewPath = localModel.GetPreviewImageFullPath(settingsManager.ModelsDirectory);
                if (File.Exists(previewPath))
                {
                    File.Delete(previewPath);
                }

                var cmInfoPath = checkpointPath.ToString().Replace(checkpointPath.Extension, ".cm-info.json");
                if (File.Exists(cmInfoPath))
                {
                    File.Delete(cmInfoPath);
                }

                await modelIndexService.RemoveModelAsync(localModel);
            }

            settingsManager.Transaction(settings => settings.InstalledModelHashes?.Remove(hash));
            fileToDelete.IsInstalled = false;
            originalSelectedVersionVm?.RefreshInstallStatus();
        }
    }

    public void PreviousImage()
    {
        if (SelectedImageIndex > 0)
            SelectedImageIndex--;
        CanGoToPreviousImage = SelectedImageIndex > 0;
        CanGoToNextImage = SelectedImageIndex < ImageUrls.Count - 1;
    }

    public void NextImage()
    {
        if (SelectedImageIndex < ImageUrls.Count - 1)
            SelectedImageIndex++;
        CanGoToPreviousImage = SelectedImageIndex > 0;
        CanGoToNextImage = SelectedImageIndex < ImageUrls.Count - 1;
    }
}
