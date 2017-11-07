// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.ComponentModel.Composition;
using System.Linq;
using Microsoft.VisualStudio.LanguageServices.Implementation.ProjectSystem;
using Microsoft.VisualStudio.LanguageServices.ProjectSystem;
using Microsoft.VisualStudio.ProjectSystem.LanguageServices;
using Microsoft.VisualStudio.ProjectSystem.LanguageServices.Handlers;
using ExportOrder = Microsoft.VisualStudio.ProjectSystem.OrderAttribute;

namespace Microsoft.VisualStudio.ProjectSystem.Razor
{
    [AppliesTo("RazorDotNetCore")]
    [ExportOrder(1000)] // Run before the default one so we can replace
    [Export(typeof(IContextHandlerProvider))]
    internal partial class RazorContextHandlerProvider : IContextHandlerProvider
    {
        private static readonly ImmutableArray<(HandlerFactory Factory, string EvaluationRuleName)> HandlerFactories = CreateHandlerFactories();
        private static readonly ImmutableArray<string> AllEvaluationRuleNames = GetEvaluationRuleNames();
        private readonly ConcurrentDictionary<IWorkspaceProjectContext, Handlers> _contextToHandlers = new ConcurrentDictionary<IWorkspaceProjectContext, Handlers>();
        private readonly UnconfiguredProject _project;

        [ImportingConstructor]
        public RazorContextHandlerProvider(UnconfiguredProject project)
        {
            Requires.NotNull(project, nameof(project));

            _project = project;
        }

        public ImmutableArray<string> EvaluationRuleNames
        {
            get { return AllEvaluationRuleNames; }
        }

        public bool AppliesTo(IWorkspaceProjectContext context)
        {
            return IsRazorCompanionProject(context);
        }

        public ImmutableArray<(IEvaluationHandler Value, string EvaluationRuleName)> GetEvaluationHandlers(IWorkspaceProjectContext context)
        {
            Requires.NotNull(context, nameof(context));

            if (!IsRazorCompanionProject(context))
            {
                return ImmutableArray.Create<(IEvaluationHandler Value, string EvaluationRuleName)>();
            }

            Handlers handlers = _contextToHandlers.GetOrAdd(context, CreateHandlers);

            return handlers.EvaluationHandlers;
        }

        public ImmutableArray<ICommandLineHandler> GetCommandLineHandlers(IWorkspaceProjectContext context)
        {
            Requires.NotNull(context, nameof(context));

            if (!IsRazorCompanionProject(context))
            {
                return ImmutableArray.Create<ICommandLineHandler>();
            }

            Handlers handlers = _contextToHandlers.GetOrAdd(context, CreateHandlers);

            return handlers.CommandLineHandlers;
        }

        public void ReleaseHandlers(IWorkspaceProjectContext context)
        {
            Requires.NotNull(context, nameof(context));

            _contextToHandlers.TryRemove(context, out _);
        }

        private Handlers CreateHandlers(IWorkspaceProjectContext context)
        {
            var evaluationHandlers = ImmutableArray.CreateBuilder<(IEvaluationHandler Value, string EvaluationRuleName)>(HandlerFactories.Length);
            var commandLineHandlers = ImmutableArray.CreateBuilder<ICommandLineHandler>(HandlerFactories.Length);

            foreach (var factory in HandlerFactories)
            {
                object handler = factory.Factory(_project, context);

                // NOTE: Handlers can be both IEvaluationHandler and ICommandLineHandler
                if (handler is IEvaluationHandler evaluationHandler)
                {
                    evaluationHandlers.Add((evaluationHandler, factory.EvaluationRuleName));
                }

                if (handler is ICommandLineHandler commandLineHandler)
                {
                    commandLineHandlers.Add(commandLineHandler);
                }
            }

            return new Handlers(evaluationHandlers.ToImmutable(), commandLineHandlers.ToImmutable());
        }

        private static ImmutableArray<(HandlerFactory Factory, string EvaluationRuleName)> CreateHandlerFactories()
        {
            return ImmutableArray.Create<(HandlerFactory Factory, string EvaluationRuleName)>(

            // Factory                                                                      EvalautionRuleName                  Description

            // Evaluation and Command-line
            ((project, context) => new CompileItemHandler(project, context), Compile.SchemaName),                // <Compile /> item
            ((project, context) => new ContentItemHandler(project, context), Content.SchemaName),                // <Content /> item

            // Evaluation only
            ((project, context) => new ProjectPropertiesItemHandler(context), ConfigurationGeneral.SchemaName),   // <ProjectGuid>, <TargetPath> properties

            // Command-line only
            ((project, context) => new MetadataReferenceItemHandler(project, context), null),                              // <ProjectReference />, <Reference /> items
            ((project, context) => new AnalyzerItemHandler(project, context), null),                              // <Analyzer /> item
            ((project, context) => new AdditionalFilesItemHandler(project, context), null)                               // <AdditionalFiles /> item
            );
        }

        private static ImmutableArray<string> GetEvaluationRuleNames()
        {
            return HandlerFactories.Select(e => e.EvaluationRuleName)
                                   .Where(name => !string.IsNullOrEmpty(name))
                                   .Distinct(StringComparers.RuleNames)
                                   .ToImmutableArray();
        }

        private bool IsRazorCompanionProject(IWorkspaceProjectContext context)
        {
            var project = ((AbstractProject)context);
            return project.ProjectSystemName.EndsWith("- Razor");
        }

        private delegate object HandlerFactory(UnconfiguredProject project, IWorkspaceProjectContext context);
    }

    partial class RazorContextHandlerProvider
    {
        private class Handlers
        {
            public readonly ImmutableArray<(IEvaluationHandler Value, string EvaluationRuleName)> EvaluationHandlers;
            public readonly ImmutableArray<ICommandLineHandler> CommandLineHandlers;

            public Handlers(ImmutableArray<(IEvaluationHandler Value, string EvaluationRuleName)> evaluationHandlers, ImmutableArray<ICommandLineHandler> commandLineHandlers)
            {
                EvaluationHandlers = evaluationHandlers;
                CommandLineHandlers = commandLineHandlers;
            }
        }
    }
}
