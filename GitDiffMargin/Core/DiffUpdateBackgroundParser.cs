using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reactive.Concurrency;
using System.Threading;
using System.Threading.Tasks;
using GitDiffMargin.Git;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Text;
using Rackspace.Threading;

namespace GitDiffMargin.Core
{
    public class DiffUpdateBackgroundParser : BackgroundParser
    {
        private readonly FileSystemWatcher _watcher;
        private readonly IGitCommands _commands;
        private readonly ITextDocument _textDocument;
        private readonly ITextBuffer _documentBuffer;

        internal DiffUpdateBackgroundParser(ITextBuffer textBuffer, ITextBuffer documentBuffer, IScheduler taskScheduler, ITextDocumentFactoryService textDocumentFactoryService, IGitCommands commands)
            : base(textBuffer, taskScheduler, textDocumentFactoryService)
        {
            _documentBuffer = documentBuffer;
            _commands = commands;
            ReparseDelay = TimeSpan.FromMilliseconds(500);

            if (TextDocumentFactoryService.TryGetTextDocument(_documentBuffer, out _textDocument))
            {
                if (_commands.IsGitRepository(_textDocument.FilePath))
                {
                    var repositoryDirectory = _commands.GetGitRepository(_textDocument.FilePath);
                    if (repositoryDirectory != null)
                    {
                        _watcher = new FileSystemWatcher(repositoryDirectory);
                        _watcher.Changed += HandleFileSystemChanged;
                        _watcher.Created += HandleFileSystemChanged;
                        _watcher.Deleted += HandleFileSystemChanged;
                        _watcher.Renamed += HandleFileSystemChanged;
                        _watcher.EnableRaisingEvents = true;
                    }
                }
            }
        }

        private void HandleFileSystemChanged(object sender, FileSystemEventArgs e)
        {
            Action action =
                () =>
                {
                    try
                    {
                        ProcessFileSystemChange(e);
                    }
                    catch (Exception ex)
                    {
                        // Failure to handle TPL exception in .NET 4 would bring down Visual Studio 2010
                        if (ErrorHandler.IsCriticalException(ex))
                            throw;
                    }
                };

            Task.Factory.StartNew(action, CancellationToken.None, TaskCreationOptions.None, TaskScheduler.Default);
        }

        private void ProcessFileSystemChange(FileSystemEventArgs e)
        {
            if (e.ChangeType == WatcherChangeTypes.Changed && Directory.Exists(e.FullPath))
                return;

            if (string.Equals(Path.GetExtension(e.Name), ".lock", StringComparison.OrdinalIgnoreCase))
                return;

            MarkDirty();
        }

        public override string Name
        {
            get
            {
                return "Git Diff Analyzer";
            }
        }

        protected override Task<ParseResultEventArgs> ReParseImplAsync(CancellationToken cancellationToken)
        {
            var stopwatch = Stopwatch.StartNew();

            var snapshot = TextBuffer.CurrentSnapshot;
            ITextDocument textDocument;
            if (!TextDocumentFactoryService.TryGetTextDocument(_documentBuffer, out textDocument))
                return CompletedTask.Canceled<ParseResultEventArgs>();

            return CompletedTask.Default.Then(_ => Task.Factory.StartNew(
                () =>
                {
                    var diff = _commands.GetGitDiffFor(textDocument, snapshot);
                    ParseResultEventArgs result = new DiffParseResultEventArgs(snapshot, stopwatch.Elapsed, diff.ToList());
                    return result;
                }));
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);

            if (disposing)
            {
                if (_watcher != null)
                {
                    _watcher.Dispose();
                }
            }
        }
    }
}