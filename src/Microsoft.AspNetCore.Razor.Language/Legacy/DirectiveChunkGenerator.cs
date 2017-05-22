// Copyright(c) .NET Foundation.All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using Microsoft.Extensions.Internal;

namespace Microsoft.AspNetCore.Razor.Language.Legacy
{
    internal class DirectiveChunkGenerator : ParentChunkGenerator
    {
        private static readonly Type Type = typeof(DirectiveChunkGenerator);
        private List<RazorDiagnostic> _diagnostics;

        public DirectiveChunkGenerator(DirectiveDescriptor descriptor)
        {
            Descriptor = descriptor;
        }

        public DirectiveDescriptor Descriptor { get; }

        public List<RazorDiagnostic> Diagnostics
        {
            get
            {
                if (_diagnostics == null)
                {
                    _diagnostics = new List<RazorDiagnostic>();
                }

                return _diagnostics;
            }
        }

        public override void Accept(ParserVisitor visitor, Block block)
        {
            visitor.VisitDirectiveBlock(this, block);
        }

        public override bool Equals(object obj)
        {
            var other = obj as DirectiveChunkGenerator;
            return base.Equals(other) &&
                DirectiveDescriptorComparer.Default.Equals(Descriptor, other.Descriptor);
        }

        public override int GetHashCode()
        {
            var combiner = HashCodeCombiner.Start();
            combiner.Add(base.GetHashCode());
            combiner.Add(Type);

            return combiner.CombinedHash;
        }
    }
}
