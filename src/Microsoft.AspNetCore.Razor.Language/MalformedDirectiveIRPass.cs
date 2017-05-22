// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Razor.Language.Intermediate;

namespace Microsoft.AspNetCore.Razor.Language
{
    internal class MalformedDirectiveIRPass : RazorIRPassBase, IRazorDirectiveClassifierPass
    {
        // -------------------------------------- REVIEWERS --------------------------------------------------
        // Should this guy override order to ensure it runs before all of our features and any that users add?
        // I'm thinking this should be -50 before default feature order (1000).

        protected override void ExecuteCore(RazorCodeDocument codeDocument, DocumentIRNode irDocument)
        {
            var visitor = new Visitor();
            visitor.VisitDocument(irDocument);

            foreach (var nodeReference in visitor.DirectiveNodes)
            {
                if (nodeReference.Node.HasDiagnostics &&
                    nodeReference.Node.Diagnostics.Any(diagnostic => diagnostic.Severity == RazorDiagnosticSeverity.Error))
                {
                    var malformedDirective = new MalformedDirectiveIRNode((DirectiveIRNode)nodeReference.Node);
                    nodeReference.Replace(malformedDirective);
                }
            }
        }

        private class Visitor : RazorIRNodeWalker
        {
            public IList<RazorIRNodeReference> DirectiveNodes { get; } = new List<RazorIRNodeReference>();

            public override void VisitDirective(DirectiveIRNode node)
            {
                DirectiveNodes.Add(new RazorIRNodeReference(Parent, node));
            }
        }
    }
}
