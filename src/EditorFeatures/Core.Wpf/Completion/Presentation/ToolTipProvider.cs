﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using Microsoft.CodeAnalysis.Classification;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.Editor.Shared.Extensions;
using Microsoft.CodeAnalysis.Editor.Shared.Utilities;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Utilities;
using VSCompletion = Microsoft.VisualStudio.Language.Intellisense.Completion;

namespace Microsoft.CodeAnalysis.Editor.Implementation.IntelliSense.Completion.Presentation
{
    [Export(typeof(IUIElementProvider<VSCompletion, ICompletionSession>))]
    [Name("RoslynToolTipProvider")]
    [ContentType(ContentTypeNames.RoslynContentType)]
    internal class ToolTipProvider : IUIElementProvider<VSCompletion, ICompletionSession>
    {
        private readonly IThreadingContext _threadingContext;
        private readonly ClassificationTypeMap _typeMap;
        private readonly IClassificationFormatMap _formatMap;

        // The textblock containing "..." that will be displayed until the actual completion 
        // description has been computed.
        private readonly TextBlock _defaultTextBlock;

        [ImportingConstructor]
        [Obsolete(MefConstruction.ImportingConstructorMessage, error: true)]
        public ToolTipProvider(IThreadingContext threadingContext, ClassificationTypeMap typeMap, IClassificationFormatMapService classificationFormatMapService)
        {
            _threadingContext = threadingContext;
            _typeMap = typeMap;
            _formatMap = classificationFormatMapService.GetClassificationFormatMap("tooltip");
            _defaultTextBlock = new TaggedText(TextTags.Text, "...").ToTextBlock(_formatMap, typeMap);
        }

        public UIElement GetUIElement(VSCompletion itemToRender, ICompletionSession context, UIElementType elementType)
        {
            var item = itemToRender as CustomCommitCompletion;
            if (item == null)
            {
                return null;
            }

            return new CancellableContentControl(this, item);
        }

        private class CancellableContentControl : ContentControl
        {
            private readonly CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();
            private readonly ToolTipProvider _toolTipProvider;

            public CancellableContentControl(ToolTipProvider toolTipProvider, CustomCommitCompletion item)
            {
                Debug.Assert(toolTipProvider._threadingContext.JoinableTaskContext.IsOnMainThread);
                _toolTipProvider = toolTipProvider;

                // Set our content to be "..." initially.
                this.Content = toolTipProvider._defaultTextBlock;

                // Kick off the task to produce the new content.  When it completes, call back on 
                // the UI thread to update the display.
                var scheduler = TaskScheduler.FromCurrentSynchronizationContext();
                item.GetDescriptionAsync(_cancellationTokenSource.Token)
                              .ContinueWith(ProcessDescription, _cancellationTokenSource.Token,
                                            TaskContinuationOptions.OnlyOnRanToCompletion, scheduler);

                // If we get unloaded (i.e. the user scrolls down in the completion list and VS 
                // dismisses the existing tooltip), then cancel the work we're doing
                this.Unloaded += (s, e) => _cancellationTokenSource.Cancel();
            }

            private void ProcessDescription(Task<CompletionDescription> obj)
            {
                Debug.Assert(_toolTipProvider._threadingContext.JoinableTaskContext.IsOnMainThread);

                // If we were canceled, or didn't run all the way to completion, then don't bother
                // updating the UI.
                if (_cancellationTokenSource.IsCancellationRequested ||
                    obj.Status != TaskStatus.RanToCompletion)
                {
                    return;
                }

                var description = obj.Result;
                this.Content = GetTextBlock(description.TaggedParts);

                // The editor will pull AutomationProperties.Name from our UIElement and expose
                // it to automation.
                AutomationProperties.SetName(this, description.Text);
            }


            public static Run GetRun(TaggedText part, IClassificationFormatMap formatMap, ClassificationTypeMap typeMap)
            {
                var text = GetVisibleDisplayString(part, includeLeftToRightMarker: true);

                var run = new Run(text);

                var format = formatMap.GetTextProperties(
                    typeMap.GetClassificationType(ClassificationTags.GetClassificationTypeName(part.Tag)));

                run.SetTextProperties(format);

                return run;
            }

            private const string LeftToRightMarkerPrefix = "\u200e";

            private static string GetVisibleDisplayString(TaggedText part, bool includeLeftToRightMarker)
            {
                var text = part.Text;

                if (includeLeftToRightMarker)
                {
                    var classificationTypeName = ClassificationTags.GetClassificationTypeName(part.Tag);
                    if (classificationTypeName == ClassificationTypeNames.Punctuation ||
                        classificationTypeName == ClassificationTypeNames.WhiteSpace)
                    {
                        text = LeftToRightMarkerPrefix + text;
                    }
                }

                return text;
            }

            private TextBlock GetTextBlock(ImmutableArray<TaggedText> parts)
            {
                var result = new TextBlock() { TextWrapping = TextWrapping.Wrap };

                result.SetDefaultTextProperties(_toolTipProvider._formatMap);

                foreach (var part in parts)
                {
                    result.Inlines.Add(GetRun(part, _toolTipProvider._formatMap, _toolTipProvider._typeMap));
                }

                if (SolutionLoadToolTip.Loading())
                {
                    result.Inlines.Add(new LineBreak());
                    result.Inlines.Add(new LineBreak());
                    result.Inlines.Add(new Run("Initializing IntelliSense …"));
                }

                return result;
            }

            private static IList<ClassificationSpan> GetClassificationSpans(
                IEnumerable<TaggedText> parts,
                ITextSnapshot textSnapshot,
                ClassificationTypeMap typeMap)
            {
                var result = new List<ClassificationSpan>();

                var index = 0;
                foreach (var part in parts)
                {
                    var text = part.ToString();
                    result.Add(new ClassificationSpan(
                        new SnapshotSpan(textSnapshot, new Microsoft.VisualStudio.Text.Span(index, text.Length)),
                        typeMap.GetClassificationType(ClassificationTags.GetClassificationTypeName(part.Tag))));

                    index += text.Length;
                }

                return result;
            }
        }
    }

    internal static class SolutionLoadToolTip
    {
        public static DateTime? _lastTimeShow;

        private static void EnsureTracking()
        {
            if (!_lastTimeShow.HasValue)
            {
                _lastTimeShow = DateTime.UtcNow;
            }
        }

        public static bool Loading()
        {
            EnsureTracking();

            return (DateTime.UtcNow - _lastTimeShow) < TimeSpan.FromMinutes(1);
        }
    }
}
