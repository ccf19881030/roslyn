﻿' Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

Imports System.ComponentModel.Composition
Imports System.Threading
Imports Microsoft.CodeAnalysis.Editor.Implementation.Highlighting
Imports Microsoft.CodeAnalysis.Text
Imports Microsoft.CodeAnalysis.VisualBasic.Syntax

Namespace Microsoft.CodeAnalysis.Editor.VisualBasic.KeywordHighlighting
    <ExportHighlighter(LanguageNames.VisualBasic)>
    Friend Class PropertyDeclarationHighlighter
        Inherits AbstractKeywordHighlighter(Of PropertyStatementSyntax)

        <ImportingConstructor>
        Public Sub New()
        End Sub

        Protected Overrides Sub AddHighlights(propertyDeclaration As PropertyStatementSyntax, highlights As List(Of TextSpan), cancellationToken As CancellationToken)
            ' If the ancestor is not a property block, treat this as an auto-property.
            ' Otherwise, let the PropertyBlockHighlighter take over.
            Dim propertyBlock = propertyDeclaration.GetAncestor(Of PropertyBlockSyntax)()
            If propertyBlock IsNot Nothing Then
                Return
            End If

            With propertyDeclaration
                Dim firstKeyword = If(.Modifiers.Count > 0, .Modifiers.First(), .DeclarationKeyword)
                highlights.Add(TextSpan.FromBounds(firstKeyword.SpanStart, .DeclarationKeyword.Span.End))

                If .ImplementsClause IsNot Nothing Then
                    highlights.Add(.ImplementsClause.ImplementsKeyword.Span)
                End If
            End With
        End Sub
    End Class
End Namespace
