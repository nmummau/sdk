// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using Microsoft.CodeAnalysis.Razor.Formatting;
using Microsoft.CodeAnalysis.Tools.Analyzers;
using Microsoft.Extensions.Logging;

namespace Microsoft.CodeAnalysis.Tools.Formatters
{
    /// <summary>
    /// Reports and removes the <c>@using</c> directives that a Razor component does not need. The issues are reported
    /// as IDE0005, so the severity that a project configures for IDE0005 in its .razor files controls them.
    /// </summary>
    internal sealed class RazorUnnecessaryUsingsFormatter : ICodeFormatter
    {
        internal const string DiagnosticId = "IDE0005";

        public FixCategory Category => FixCategory.CodeStyle;

        public async Task<Solution> FormatAsync(
            Workspace workspace,
            Solution solution,
            ImmutableArray<DocumentId> formattableDocuments,
            FormatOptions formatOptions,
            ILogger logger,
            List<FormattedFile> formattedFiles,
            CancellationToken cancellationToken)
        {
            if ((!formatOptions.Diagnostics.IsEmpty && !formatOptions.Diagnostics.Contains(DiagnosticId)) ||
                formatOptions.ExcludeDiagnostics.Contains(DiagnosticId))
            {
                return solution;
            }

            var formattedSolution = solution;
            var analyzerConfigSets = new Dictionary<ProjectId, AnalyzerConfigSet>();

            foreach (var documentId in formattableDocuments)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var document = formattedSolution.GetAdditionalDocument(documentId);
                if (document is null || !FormatterUtilities.IsRazorDocument(document) || document.FilePath is null)
                {
                    continue;
                }

                if (!analyzerConfigSets.TryGetValue(document.Project.Id, out var analyzerConfigSet))
                {
                    analyzerConfigSet = await CreateAnalyzerConfigSetAsync(document.Project, cancellationToken);
                    analyzerConfigSets.Add(document.Project.Id, analyzerConfigSet);
                }

                var severity = GetSeverity(analyzerConfigSet, document.FilePath);
                if (severity == DiagnosticSeverity.Hidden || severity < formatOptions.CodeStyleSeverity)
                {
                    continue;
                }

                var spans = await RazorUnnecessaryDirectives.GetUnnecessaryUsingDirectiveSpansAsync(document.Project, document.Id, cancellationToken);
                if (spans.IsEmpty)
                {
                    continue;
                }

                var text = await document.GetTextAsync(cancellationToken);
                var severityText = severity.ToString().ToLower();
                var fileChanges = ImmutableArray.CreateBuilder<FileChange>(spans.Length);

                foreach (var span in spans)
                {
                    var position = text.Lines.GetLinePosition(span.Start);
                    if (!formatOptions.SaveFormattedFiles || formatOptions.LogLevel == LogLevel.Debug)
                    {
                        logger.LogDiagnosticIssue(document, position, severityText, DiagnosticId, Resources.Using_directive_is_unnecessary, formatOptions.ChangesAreErrors);
                    }

                    fileChanges.Add(new FileChange(position, DiagnosticId, $"{severityText} {DiagnosticId}: {Resources.Using_directive_is_unnecessary}"));
                }

                formattedFiles.Add(new FormattedFile(document, fileChanges.MoveToImmutable()));

                if (!formatOptions.SaveFormattedFiles)
                {
                    continue;
                }

                var fixedText = await RazorUnnecessaryDirectives.RemoveUnnecessaryUsingDirectivesAsync(document.Project, document.Id, cancellationToken);
                if (fixedText is not null)
                {
                    formattedSolution = formattedSolution.WithAdditionalDocumentText(document.Id, fixedText, PreservationMode.PreserveIdentity);
                }
            }

            return formattedSolution;
        }

        /// <summary>
        /// Gets the analyzer configuration of the project. The compiler keeps the configured severity of a diagnostic
        /// out of the options that it gives to analyzers, so the severity for a .razor file must come from this set.
        /// </summary>
        private static async Task<AnalyzerConfigSet> CreateAnalyzerConfigSetAsync(Project project, CancellationToken cancellationToken)
        {
            var analyzerConfigs = new List<AnalyzerConfig>();

            foreach (var analyzerConfigDocument in project.AnalyzerConfigDocuments)
            {
                if (analyzerConfigDocument.FilePath is null)
                {
                    continue;
                }

                var text = await analyzerConfigDocument.GetTextAsync(cancellationToken);
                analyzerConfigs.Add(AnalyzerConfig.Parse(text, analyzerConfigDocument.FilePath));
            }

            return AnalyzerConfigSet.Create(analyzerConfigs);
        }

        /// <summary>
        /// Gets the severity that the analyzer configuration sets for IDE0005 in the file. The default severity of
        /// IDE0005 is hidden, so a project that does not configure it gets no reports.
        /// </summary>
        private static DiagnosticSeverity GetSeverity(AnalyzerConfigSet analyzerConfigSet, string filePath)
            => analyzerConfigSet.GetOptionsForSourcePath(filePath).TreeOptions.TryGetValue(DiagnosticId, out var reportDiagnostic)
                ? reportDiagnostic.ToSeverity()
                : DiagnosticSeverity.Hidden;
    }
}
