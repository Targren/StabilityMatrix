﻿using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using AsyncAwaitBestPractices;
using AutoComplete.Builders;
using AutoComplete.Clients.IndexSearchers;
using AutoComplete.DataStructure;
using AutoComplete.Domain;
using Avalonia.Controls.Notifications;
using Nito.AsyncEx;
using NLog;
using StabilityMatrix.Avalonia.Controls.CodeCompletion;
using StabilityMatrix.Avalonia.Helpers;
using StabilityMatrix.Avalonia.Services;
using StabilityMatrix.Core.Helper;
using StabilityMatrix.Core.Models;
using StabilityMatrix.Core.Models.FileInterfaces;
using StabilityMatrix.Core.Models.Settings;
using StabilityMatrix.Core.Services;

namespace StabilityMatrix.Avalonia.Models.TagCompletion;

public class CompletionProvider : ICompletionProvider
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private readonly INotificationService notificationService;
    
    private readonly AsyncLock loadLock = new();
    private readonly Dictionary<string, TagCsvEntry> entries = new();
    
    private InMemoryIndexSearcher? searcher;
    
    public bool IsLoaded => searcher is not null;

    public CompletionProvider(ISettingsManager settingsManager, INotificationService notificationService)
    {
        this.notificationService = notificationService;

        // Attach to load from set file on initial settings load
        settingsManager.Loaded += (_, _) => UpdateTagCompletionCsv();
        
        // Also load when TagCompletionCsv property changes
        settingsManager.SettingsPropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(Settings.TagCompletionCsv))
            {
                UpdateTagCompletionCsv();
            }
        };
        
        // If library already loaded, start a background load
        if (settingsManager.IsLibraryDirSet)
        {
            UpdateTagCompletionCsv();
        }
        
        return;

        void UpdateTagCompletionCsv()
        {
            var csvPath = settingsManager.Settings.TagCompletionCsv;
            if (csvPath is null) return;

            var fullPath = settingsManager.TagsDirectory.JoinFile(csvPath);
            BackgroundLoadFromFile(fullPath);
        }
    }

    /// <inheritdoc />
    public void BackgroundLoadFromFile(FilePath path, bool recreate = false)
    {
        LoadFromFile(path, recreate).SafeFireAndForget(onException: exception =>
        {
            const string title = "Failed to load tag completion file";
            Debug.Fail(title);
            Logger.Warn(exception, title);
            notificationService.Show(title + $" {path.Name}", 
                exception.Message, NotificationType.Error);
        }, true);
    }
    
    /// <inheritdoc />
    public async Task LoadFromFile(FilePath path, bool recreate = false)
    {
        using var _ = await loadLock.LockAsync();
        
        // Get Blake3 hash of file
        var hash = await FileHash.GetBlake3Async(path);
        
        Logger.Trace("Loading tags from {Path} with Blake3 hash {Hash}", path, hash);
        
        // Check for AppData/StabilityMatrix/Temp/Tags/<hash>/*.bin
        var tempTagsDir = GlobalConfig.HomeDir.JoinDir("Temp", "Tags");
        var hashDir = tempTagsDir.JoinDir(hash);
        hashDir.Create();
        
        var headerFile = hashDir.JoinFile("header.bin");
        var indexFile = hashDir.JoinFile("index.bin");

        entries.Clear();
        
        var timer = Stopwatch.StartNew();
        
        // If directory or any file is missing, rebuild the index
        if (recreate || !(hashDir.Exists && headerFile.Exists && indexFile.Exists))
        {
            Logger.Debug("Creating new index for {Path}", hashDir);
            
            await using var headerStream = headerFile.Info.OpenWrite();
            await using var indexStream = indexFile.Info.OpenWrite();
            
            var builder = new IndexBuilder(headerStream, indexStream);
            
            // Parse csv
            await using var csvStream = path.Info.OpenRead();
            var parser = new TagCsvParser(csvStream);

            await foreach (var entry in parser.ParseAsync())
            {
                if (string.IsNullOrWhiteSpace(entry.Name)) continue;
                
                // Add to index
                builder.Add(entry.Name);
                // Add to local dictionary
                entries.Add(entry.Name, entry);
            }
            
            await Task.Run(builder.Build);
        }
        else
        {
            // Otherwise just load the dictionary
            Logger.Debug("Loading existing index for {Path}", hashDir);

            await using var csvStream = path.Info.OpenRead();
            var parser = new TagCsvParser(csvStream);
            
            await foreach (var entry in parser.ParseAsync())
            {
                if (string.IsNullOrWhiteSpace(entry.Name)) continue;
                
                // Add to local dictionary
                entries.Add(entry.Name, entry);
            }
        }
        
        searcher = new InMemoryIndexSearcher(headerFile, indexFile);
        searcher.Init();
        
        var elapsed = timer.Elapsed;
        
        Logger.Info("Loaded {Count} tags for {Path} in {Time:F2}s", entries.Count, path.Name, elapsed.TotalSeconds);
    }

    /// <inheritdoc />
    public IEnumerable<ICompletionData> GetCompletions(string searchTerm, int itemsCount, bool suggest)
    {
        return GetCompletionsImpl_Index(searchTerm, itemsCount, suggest);
    }

    private IEnumerable<ICompletionData> GetCompletionsImpl_Fuzzy(string searchTerm, int itemsCount, bool suggest)
    {
        var extracted = FuzzySharp.Process
            .ExtractTop(searchTerm, entries.Keys);
        
        var results = extracted
            .Where(r => r.Score > 40)
            .Select(r => r.Value)
            .ToImmutableArray();
        
        // No results
        if (results.IsEmpty)
        {
            Logger.Trace("No results for {Term}", searchTerm);
            return Array.Empty<ICompletionData>();
        }
        
        Logger.Trace("Got {Count} results for {Term}", results.Length, searchTerm);
        
        // Get entry for each result
        var completions = new List<ICompletionData>();
        foreach (var item in results)
        {
            if (entries.TryGetValue(item, out var entry))
            {
                var entryType = TagTypeExtensions.FromE621(entry.Type.GetValueOrDefault(-1));
                completions.Add(new TagCompletionData(entry.Name!, entryType)
                {
                    Priority = entry.Count ?? 0
                });
            }
        }
        
        return completions;
    }
    
    private IEnumerable<ICompletionData> GetCompletionsImpl_Index(string searchTerm, int itemsCount, bool suggest)
    {
        if (searcher is null)
        {
            throw new InvalidOperationException("Index is not loaded");
        }
        
        var timer = Stopwatch.StartNew();
        
        var searchOptions = new SearchOptions
        {
            Term = searchTerm,
            MaxItemCount = itemsCount,
            SuggestWhenFoundStartsWith = suggest
        };
        
        var result = searcher.Search(searchOptions);

        // No results
        if (result.ResultType == TrieNodeSearchResultType.NotFound)
        {
            Logger.Trace("No results for {Term}", searchTerm);
            return Array.Empty<ICompletionData>();
        }
        
        // Is null for some reason?
        if (result.Items is null)
        {
            Logger.Warn("Got null results for {Term}", searchTerm);
            return Array.Empty<ICompletionData>();
        }
        
        Logger.Trace("Got {Count} results for {Term}", result.Items.Length, searchTerm);
        
        // Get entry for each result
        var completions = new List<ICompletionData>();
        foreach (var item in result.Items)
        {
            if (entries.TryGetValue(item, out var entry))
            {
                var entryType = TagTypeExtensions.FromE621(entry.Type.GetValueOrDefault(-1));
                completions.Add(new TagCompletionData(entry.Name!, entryType));
            }
        }
        
        timer.Stop();
        Logger.Trace("Completions for {Term} took {Time:F2}ms", searchTerm, timer.Elapsed.TotalMilliseconds);
        
        return completions;
    }
}
