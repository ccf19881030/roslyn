﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System.Threading;

namespace Microsoft.CodeAnalysis.IntroduceVariable
{
    internal partial class AbstractIntroduceVariableService<TService, TExpressionSyntax, TTypeSyntax, TTypeDeclarationSyntax, TQueryExpressionSyntax, TNameSyntax>
    {
        private partial class State
        {
            private bool IsInAttributeContext()
            {
                if (!_service.IsInAttributeArgumentInitializer(Expression))
                {
                    return false;
                }

                // Have to make sure we're on or inside a type decl so that we have some place to
                // put the result.
                return IsInTypeDeclarationOrValidCompilationUnit();
            }
        }
    }
}
