// Copyright(c) .NET Foundation.All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System.Collections.Generic;
using System.Linq;

namespace Microsoft.AspNetCore.Razor.Language.Intermediate
{
    public sealed class MalformedDirectiveIRNode : RazorIRNode
    {
        public MalformedDirectiveIRNode(DirectiveIRNode directive)
        {
            Annotations = new DefaultItemCollection();

            foreach (var annotation in directive.Annotations)
            {
                Annotations.Add(annotation);
            }

            Diagnostics = new DefaultDiagnosticCollection();

            for (var i = 0; i < directive.Diagnostics.Count; i++)
            {
                Diagnostics.Add(directive.Diagnostics[i]);
            }

            for (var i = 0; i < directive.Children.Count; i++)
            {
                Children.Add(directive.Children[i]);
            }

            Source = directive.Source;
            Name = directive.Name;
            Descriptor = directive.Descriptor;
        }

        public override ItemCollection Annotations { get; }

        public override RazorDiagnosticCollection Diagnostics { get; }

        public override RazorIRNodeCollection Children { get; } = new DefaultIRNodeCollection();

        public override SourceSpan? Source { get; set; }

        public override bool HasDiagnostics => Diagnostics.Count > 0;

        public string Name { get; set; }

        public IEnumerable<DirectiveTokenIRNode> Tokens => Children.OfType<DirectiveTokenIRNode>();

        public DirectiveDescriptor Descriptor { get; set; }

        public override void Accept(RazorIRNodeVisitor visitor)
        {
            visitor.VisitMalformedDirective(this);
        }
    }
}