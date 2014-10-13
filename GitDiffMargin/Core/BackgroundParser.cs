/* The MIT License
 *
 * Copyright (c) 2013 Sam Harwell, Tunnel Vision Labs, LLC
 *
 * Permission is hereby granted, free of charge, to any person obtaining
 * a copy of this software and associated documentation files (the
 * "Software"), to deal in the Software without restriction, including
 * without limitation the rights to use, copy, modify, merge, publish,
 * distribute, sublicense, and/or sell copies of the Software, and to
 * permit persons to whom the Software is furnished to do so, subject to
 * the following conditions:
 *
 * The above copyright notice and this permission notice shall be
 * included in all copies or substantial portions of the Software.
 *
 * THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
 * EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
 * MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
 * NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
 * LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
 * OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
 * WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
 */

using System;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Text;
using Rackspace.Threading;

namespace GitDiffMargin.Core
{
    public abstract class BackgroundParser : IDisposable
    {
        private readonly WeakReference<ITextBuffer> _textBuffer;
        private readonly IScheduler _taskScheduler;
        private readonly ITextDocumentFactoryService _textDocumentFactoryService;

        private readonly IObservable<bool> _dirty;
        private readonly IObservable<ParseResultEventArgs> _parseResults;
        private readonly ISubject<bool> _parsing = new BehaviorSubject<bool>(false);
        private readonly ISubject<EventPattern<EventArgs>> _manualDelayTrigger = new BehaviorSubject<EventPattern<EventArgs>>(new EventPattern<EventArgs>(null, EventArgs.Empty));
        private readonly ISubject<EventPattern<EventArgs>> _manualImmediateTrigger = new Subject<EventPattern<EventArgs>>();

        private TimeSpan _reparseDelay;
        private bool _disposed;

        protected BackgroundParser(ITextBuffer textBuffer, IScheduler taskScheduler, ITextDocumentFactoryService textDocumentFactoryService)
        {
            if (textBuffer == null)
                throw new ArgumentNullException("textBuffer");
            if (taskScheduler == null)
                throw new ArgumentNullException("taskScheduler");
            if (textDocumentFactoryService == null)
                throw new ArgumentNullException("textDocumentFactoryService");

            _textBuffer = new WeakReference<ITextBuffer>(textBuffer);
            _taskScheduler = taskScheduler;
            _textDocumentFactoryService = textDocumentFactoryService;

            _reparseDelay = TimeSpan.FromMilliseconds(1500);

            IObservable<EventPattern<EventArgs>> postChanged = Observable.FromEventPattern(e => textBuffer.PostChanged += e, e => textBuffer.PostChanged -= e);
            IObservable<EventPattern<EventArgs>> throttledTriggers = Observable.Merge(postChanged, _manualDelayTrigger).Throttle(DelayThrottle);
            IObservable<object> parseRequest = Observable.Merge(throttledTriggers, _manualImmediateTrigger).Throttle(ParsingThrottle);
            IObservable<object> setDirty =  Observable.Merge(postChanged, _manualDelayTrigger, _manualImmediateTrigger);

            _parseResults = parseRequest.Select(_ => Observable.FromAsync(ReParseAsync)).Switch();
            _dirty = Observable.Merge(setDirty.Select(_ => true), ParseResults.Select(_ => false)).DistinctUntilChanged();
        }

        public ITextBuffer TextBuffer
        {
            get
            {
                return _textBuffer.Target;
            }
        }

        public bool Disposed
        {
            get
            {
                return _disposed;
            }
        }

        public TimeSpan ReparseDelay
        {
            get
            {
                return _reparseDelay;
            }

            set
            {
                if (value < TimeSpan.Zero)
                    throw new ArgumentOutOfRangeException("value");

                _reparseDelay = value;
            }
        }

        public IObservable<bool> Dirty
        {
            get
            {
                return _dirty.AsObservable();
            }
        }

        public IObservable<ParseResultEventArgs> ParseResults
        {
            get
            {
                return _parseResults;
            }
        }

        protected virtual IObservable<bool> DelayThrottle(EventPattern<EventArgs> trigger)
        {
            return _parsing.Where(isParsing => !isParsing).Throttle(ReparseDelay);
        }

        protected virtual IObservable<bool> ParsingThrottle(EventPattern<EventArgs> trigger)
        {
            return _parsing.Where(isParsing => !isParsing);
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        public virtual string Name
        {
            get
            {
                return string.Empty;
            }
        }

        protected ITextDocumentFactoryService TextDocumentFactoryService
        {
            get
            {
                return _textDocumentFactoryService;
            }
        }

        protected virtual void Dispose(bool disposing)
        {
            _disposed = true;
        }

        protected abstract Task<ParseResultEventArgs> ReParseImplAsync(CancellationToken cancellationToken);

        protected void MarkDirty()
        {
            _manualDelayTrigger.OnNext(new EventPattern<EventArgs>(this, EventArgs.Empty));
        }

        private Task<ParseResultEventArgs> ReParseAsync(CancellationToken cancellationToken)
        {
            return
                CompletedTask.Default
                .Select(_ => _parsing.OnNext(true))
                .Then(_ => ReParseImplAsync(cancellationToken))
                .Finally(_ => _parsing.OnNext(false));
        }
    }
}
