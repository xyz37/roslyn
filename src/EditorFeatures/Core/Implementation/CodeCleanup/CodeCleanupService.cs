﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Editor.Shared.Extensions;
using Microsoft.CodeAnalysis.Editor.Shared.Options;
using Microsoft.CodeAnalysis.Formatting.Rules;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.Internal.Log;
using Microsoft.CodeAnalysis.Options;
using Microsoft.CodeAnalysis.OrganizeImports;
using Microsoft.CodeAnalysis.RemoveUnnecessaryImports;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Microsoft.CodeAnalysis.Shared.Utilities;
using Microsoft.CodeAnalysis.Text;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.Editor.Implementation.CodeCleanup
{
    [Export(typeof(ICodeCleanupService))]
    internal class CodeCleanupService : ICodeCleanupService
    {
        /// <summary>
        /// Maps format document code cleanup options to DiagnosticId[]
        /// </summary>
        private static ImmutableDictionary<PerLanguageOption<bool>, string[]> _dictionary = GetCodeCleanupOptionMapping();

        private readonly ICodeFixService _codeFixService;

        [ImportingConstructor]
        [Obsolete(MefConstruction.ImportingConstructorMessage, error: true)]
        public CodeCleanupService(
            ICodeFixService codeFixService)
        {
            _codeFixService = codeFixService;
        }
        public static IDictionary<PerLanguageOption<bool>, string[]> Dictionary
        {
            get
            {
                return _dictionary;
            }
        }

        private static ImmutableDictionary<PerLanguageOption<bool>, string[]> GetCodeCleanupOptionMapping()
        {
            var dictionary = new Dictionary<PerLanguageOption<bool>, string[]>();
            dictionary.Add(FeatureOnOffOptions.FixImplicitExplicitType,
                new[] { IDEDiagnosticIds.UseImplicitTypeDiagnosticId, IDEDiagnosticIds.UseExplicitTypeDiagnosticId });
            dictionary.Add(FeatureOnOffOptions.FixThisQualification,
                new[] { IDEDiagnosticIds.AddQualificationDiagnosticId, IDEDiagnosticIds.RemoveQualificationDiagnosticId });
            dictionary.Add(FeatureOnOffOptions.FixFrameworkTypes,
                new[] { IDEDiagnosticIds.PreferFrameworkTypeInDeclarationsDiagnosticId, IDEDiagnosticIds.PreferFrameworkTypeInMemberAccessDiagnosticId });
            dictionary.Add(FeatureOnOffOptions.FixAddRemoveBraces,
                new[] { IDEDiagnosticIds.AddBracesDiagnosticId });
            dictionary.Add(FeatureOnOffOptions.FixAccessibilityModifiers,
                new[] { IDEDiagnosticIds.AddAccessibilityModifiersDiagnosticId });
            dictionary.Add(FeatureOnOffOptions.SortAccessibilityModifiers,
                new[] { IDEDiagnosticIds.OrderModifiersDiagnosticId });
            dictionary.Add(FeatureOnOffOptions.MakeReadonly,
                new[] { IDEDiagnosticIds.MakeFieldReadonlyDiagnosticId });
            dictionary.Add(FeatureOnOffOptions.RemoveUnnecessaryCasts,
                new[] { IDEDiagnosticIds.RemoveUnnecessaryCastDiagnosticId });
            dictionary.Add(FeatureOnOffOptions.FixExpressionBodiedMembers,
                new[] { IDEDiagnosticIds.UseExpressionBodyForConstructorsDiagnosticId,
                IDEDiagnosticIds.UseExpressionBodyForMethodsDiagnosticId,
                IDEDiagnosticIds.UseExpressionBodyForConversionOperatorsDiagnosticId,
                IDEDiagnosticIds.UseExpressionBodyForOperatorsDiagnosticId,
                IDEDiagnosticIds.UseExpressionBodyForPropertiesDiagnosticId,
                IDEDiagnosticIds.UseExpressionBodyForIndexersDiagnosticId,
                IDEDiagnosticIds.UseExpressionBodyForAccessorsDiagnosticId });
            dictionary.Add(FeatureOnOffOptions.FixInlineVariableDeclarations,
                new[] { IDEDiagnosticIds.InlineDeclarationDiagnosticId });
            dictionary.Add(FeatureOnOffOptions.RemoveUnusedVariables,
                new[] { "CS0168", "CS0219" });
            dictionary.Add(FeatureOnOffOptions.FixObjectCollectionInitialization,
                new[] { IDEDiagnosticIds.UseObjectInitializerDiagnosticId, IDEDiagnosticIds.UseCollectionInitializerDiagnosticId });
            //dictionary.Add(FeatureOnOffOptions.FixLanguageFeatures,
            //    new[] { IDEDiagnosticIds. });
            //IDEDiagnosticIds.PreferIntrinsicPredefinedTypeInDeclarationsDiagnosticId,
            //    IDEDiagnosticIds.PreferIntrinsicPredefinedTypeInMemberAccessDiagnosticId,
            //    IDEDiagnosticIds.InlineAsTypeCheckId,
            //    IDEDiagnosticIds.InlineIsTypeCheckId,
            //    IDEDiagnosticIds.InlineIsTypeWithoutNameCheckDiagnosticsId,

            return dictionary.ToImmutableDictionary();
        }

        public Task<IEnumerable<TextChange>> GetChangesForCleanupDocument(Document document, CancellationToken cancellationToken)
        {
            var oldDocument = document;
            document = RemoveSortUsings(document, cancellationToken);
            document = ApplyCodeFixes(document, cancellationToken);

            return document.GetTextChangesAsync(oldDocument, cancellationToken);
        }
        private Document RemoveSortUsings(Document document, CancellationToken cancellationToken)
        {
            // remove and sort usings
            if (document.Project.Solution.Workspace.Options.GetOption(FeatureOnOffOptions.RemoveUnusedUsings, LanguageNames.CSharp))
            {
                var removeUsingsService = document.GetLanguageService<IRemoveUnnecessaryImportsService>();
                if (removeUsingsService != null)
                {
                    document = removeUsingsService.RemoveUnnecessaryImportsAsync(document, cancellationToken).WaitAndGetResult(cancellationToken);
                }
            }

            // sort usings
            if (document.Project.Solution.Workspace.Options.GetOption(FeatureOnOffOptions.SortUsings, LanguageNames.CSharp))
            {
                document = OrganizeImportsService.OrganizeImportsAsync(document, cancellationToken).WaitAndGetResult(cancellationToken);
            }

            return document;
        }

        private Document ApplyCodeFixes(Document document, CancellationToken cancellationToken)
        {
            var fixAllService = document.Project.Solution.Workspace.Services.GetService<IFixAllGetFixesService>();

            var dummy = new ProgressTracker();
            foreach (var diagnosticId in GetEnabledDiagnosticIds(document.Project.Solution.Workspace))
            {
                var length = document.GetSyntaxTreeAsync(cancellationToken).WaitAndGetResult(cancellationToken).Length;
                var textSpan = new TextSpan(0, length);

                var fixCollection = _codeFixService.GetFixesAsync(document, textSpan, diagnosticId, cancellationToken).WaitAndGetResult(cancellationToken);
                if (fixCollection == null)
                {
                    continue;
                }

                var fixAll = fixCollection.FixAllState;
                var solution = fixAllService.GetFixAllChangedSolutionAsync(fixAll.CreateFixAllContext(dummy, cancellationToken)).WaitAndGetResult(cancellationToken);
                document = solution.GetDocument(document.Id);
            }

            return document;
        }


        private List<string> GetEnabledDiagnosticIds(Workspace workspace)
        {
            var diagnosticIds = new List<string>();

            foreach (var featureOption in CodeCleanupService.Dictionary.Keys)
            {
                if (workspace.Options.GetOption(featureOption, LanguageNames.CSharp))
                {
                    diagnosticIds.AddRange(CodeCleanupService.Dictionary[featureOption]);
                }
            }

            return diagnosticIds;
        }
    }
}
