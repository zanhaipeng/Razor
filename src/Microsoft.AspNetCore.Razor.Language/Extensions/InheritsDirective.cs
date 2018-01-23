﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using Microsoft.AspNetCore.Razor.Language.Legacy;

namespace Microsoft.AspNetCore.Razor.Language.Extensions
{
    public static class InheritsDirective
    {
        public static readonly DirectiveDescriptor Directive = DirectiveDescriptor.CreateDirective(
            SyntaxConstants.CSharp.InheritsKeyword,
            DirectiveKind.SingleLine,
            builder =>
            {
                builder.AddTypeToken(Resources.InheritsDirective_TypeToken_Name, Resources.InheritsDirective_TypeToken_Description);
                builder.Usage = DirectiveUsage.FileScopedSinglyOccurring;
                builder.Description = Resources.InheritsDirective_Description;
            });

        public static void Register(IRazorEngineBuilder builder)
        {
            if (builder == null)
            {
                throw new ArgumentNullException(nameof(builder));
            }

            // ---------------------------------------------------------------------------------------------
            // When updating these registrations also update the RazorProjectEngineBuilder overload as well.
            // ---------------------------------------------------------------------------------------------

            builder.AddDirective(Directive);
            builder.Features.Add(new InheritsDirectivePass());
        }

        public static void Register(RazorProjectEngineBuilder builder)
        {
            if (builder == null)
            {
                throw new ArgumentNullException(nameof(builder));
            }

            // ----------------------------------------------------------------------------------------------------------
            // When updating the RazorEngine specific registrations also update the IRazorEngineBuilder overload as well.
            // ----------------------------------------------------------------------------------------------------------

            builder.AddDirective(Directive);
            builder.Features.Add(new InheritsDirectivePass());
        }
    }
}
