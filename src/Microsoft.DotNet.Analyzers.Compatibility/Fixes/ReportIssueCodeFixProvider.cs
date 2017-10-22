﻿using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.DotNet.Analyzers.Compatibility.Deprecated;
using Microsoft.DotNet.Analyzers.Compatibility.Exceptions;
using Microsoft.DotNet.Analyzers.Compatibility.ModernSdk;
using Microsoft.DotNet.Analyzers.Compatibility.Net461;

namespace Microsoft.DotNet.Analyzers.Compatibility.Fixes
{
    [ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(ReportIssueCodeFixProvider))]
    [Shared]
    public sealed class ReportIssueCodeFixProvider : CodeFixProvider
    {
        public sealed override ImmutableArray<string> FixableDiagnosticIds
        {
            get
            {
                var result = new List<string>();
                result.Add(ExceptionAnalyzer.DiagnosticId);
                result.Add(ModernSdkAnalyzer.DiagnosticId);
                result.Add(Net461Analyzer.DiagnosticId);
                result.AddRange(DeprecatedAnalyzer.GetDescriptors().Select(d => d.Id));
                return result.ToImmutableArray();
            }
        }

        public sealed override Task RegisterCodeFixesAsync(CodeFixContext context)
        {
            var diagnostic = context.Diagnostics.First();
            var diagnosticSpan = diagnostic.Location.SourceSpan;

            var issueTitle = $"{diagnostic.Id}: {diagnostic.GetMessage()}";
            var issueTitleEncoded = WebUtility.UrlEncode(issueTitle);
            var url = $"https://github.com/dotnet/platform-compat/issues/new?title={issueTitleEncoded}";

            var action = new OpenInBrowserAction(Resources.ReportAnIssueTitle, url);
            context.RegisterCodeFix(action, diagnostic);

            return Task.CompletedTask;
        }
        
        private sealed class OpenInBrowserAction : CodeAction
        {
            public OpenInBrowserAction(string title, string url)
            {
                Title = title;
                Url = url;
            }

            public override string Title { get; }

            public override string EquivalenceKey => Title;

            public string Url { get; }

            protected override Task<IEnumerable<CodeActionOperation>> ComputeOperationsAsync(CancellationToken cancellationToken)
            {
                var result = new[] { new OpenInBrowserOperation(Url) };
                return Task.FromResult<IEnumerable<CodeActionOperation>>(result);
            }
        }

        private sealed class OpenInBrowserOperation : CodeActionOperation
        {
            public OpenInBrowserOperation(string url)
            {
                Url = url;
            }

            public string Url { get; }

            public override void Apply(Workspace workspace, CancellationToken cancellationToken)
            {
                // NOTE: This is a hack. Unfortunately, retargeting to .NET Standard 1.6/2.0 isn't straight forward
                //       because this code has to run in VS and we don't have a good way to provide the facades and
                //       binding redirects there.

                var processClass = Type.GetType("System.Diagnostics.Process, System, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089");
                var processClassTypeInfo = processClass.GetTypeInfo();
                var startMethod = processClassTypeInfo
                                    .GetDeclaredMethods("Start")
                                    .Single(m => m.IsStatic &&
                                                m.GetParameters().Length == 1 &&
                                                m.GetParameters()[0].ParameterType == typeof(string));

                startMethod.Invoke(null, new[] { Url });
            }
        }
    }
}
