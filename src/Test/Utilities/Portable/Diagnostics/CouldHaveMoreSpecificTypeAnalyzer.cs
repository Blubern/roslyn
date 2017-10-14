﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Semantics;

namespace Microsoft.CodeAnalysis.Test.Utilities
{
    /// <summary>Analyzer used to identify local variables and fields that could be declared with more specific types.</summary>
    public class SymbolCouldHaveMoreSpecificTypeAnalyzer : DiagnosticAnalyzer
    {
        private const string SystemCategory = "System";

        public static readonly DiagnosticDescriptor LocalCouldHaveMoreSpecificTypeDescriptor = new DiagnosticDescriptor(
            "LocalCouldHaveMoreSpecificType",
            "Local Could Have More Specific Type",
            "Local variable {0} could be declared with more specific type {1}.",
            SystemCategory,
            DiagnosticSeverity.Warning,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor FieldCouldHaveMoreSpecificTypeDescriptor = new DiagnosticDescriptor(
           "FieldCouldHaveMoreSpecificType",
           "Field Could Have More Specific Type",
           "Field {0} could be declared with more specific type {1}.",
           SystemCategory,
           DiagnosticSeverity.Warning,
           isEnabledByDefault: true);

        /// <summary>Gets the set of supported diagnostic descriptors from this analyzer.</summary>
        public sealed override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics
        {
            get { return ImmutableArray.Create(LocalCouldHaveMoreSpecificTypeDescriptor, FieldCouldHaveMoreSpecificTypeDescriptor); }
        }

        public sealed override void Initialize(AnalysisContext context)
        {
            context.RegisterCompilationStartAction(
                (compilationContext) =>
                {
                    Dictionary<IFieldSymbol, HashSet<INamedTypeSymbol>> fieldsSourceTypes = new Dictionary<IFieldSymbol, HashSet<INamedTypeSymbol>>();

                    compilationContext.RegisterOperationBlockStartAction(
                        (operationBlockContext) =>
                        {
                            if (operationBlockContext.OwningSymbol is IMethodSymbol containingMethod)
                            {
                                Dictionary<ILocalSymbol, HashSet<INamedTypeSymbol>> localsSourceTypes = new Dictionary<ILocalSymbol, HashSet<INamedTypeSymbol>>();

                                // Track explicit assignments.
                                operationBlockContext.RegisterOperationAction(
                                   (operationContext) =>
                                   {
                                       if (operationContext.Operation is IAssignmentExpression assignment)
                                       {
                                           AssignTo(assignment.Target, localsSourceTypes, fieldsSourceTypes, assignment.Value);
                                       }
                                       else if (operationContext.Operation is IIncrementOrDecrementExpression increment)
                                       {
                                           SyntaxNode syntax = increment.Syntax;
                                           ITypeSymbol type = increment.Type;
                                           Optional<object> constantValue = new Optional<object>(1);
                                           bool isImplicit = increment.IsImplicit;
                                           var value = new LiteralExpression(operationContext.Compilation.GetSemanticModel(syntax.SyntaxTree), syntax, type, constantValue, isImplicit);

                                           AssignTo(increment.Target, localsSourceTypes, fieldsSourceTypes, value);
                                       }
                                       else
                                       {
                                           throw TestExceptionUtilities.UnexpectedValue(operationContext.Operation);
                                       }
                                   },
                                   OperationKind.SimpleAssignmentExpression,
                                   OperationKind.CompoundAssignmentExpression,
                                   OperationKind.IncrementExpression);

                                // Track arguments that match out or ref parameters.
                                operationBlockContext.RegisterOperationAction(
                                    (operationContext) =>
                                    {
                                        IInvocationExpression invocation = (IInvocationExpression)operationContext.Operation;
                                        foreach (IArgument argument in invocation.ArgumentsInEvaluationOrder)
                                        {
                                            if (argument.Parameter.RefKind == RefKind.Out || argument.Parameter.RefKind == RefKind.Ref)
                                            {
                                                AssignTo(argument.Value, localsSourceTypes, fieldsSourceTypes, argument.Parameter.Type);
                                            }
                                        }
                                    },
                                    OperationKind.InvocationExpression);

                                // Track local variable initializations.
                                operationBlockContext.RegisterOperationAction(
                                    (operationContext) =>
                                    {
                                        IVariableInitializer initializer = (IVariableInitializer)operationContext.Operation;
                                        if (initializer.Parent is IVariableDeclaration variableDeclaration)
                                        {
                                            foreach (ILocalSymbol local in variableDeclaration.Variables)
                                            {
                                                AssignTo(local, local.Type, localsSourceTypes, initializer.Value);
                                            }
                                        }
                                    },
                                    OperationKind.VariableInitializer);

                                // Report locals that could have more specific types.
                                operationBlockContext.RegisterOperationBlockEndAction(
                                    (operationBlockEndContext) =>
                                    {
                                        foreach (ILocalSymbol local in localsSourceTypes.Keys)
                                        {
                                            if (HasMoreSpecificSourceType(local, local.Type, localsSourceTypes, out var mostSpecificSourceType))
                                            {
                                                Report(operationBlockEndContext, local, mostSpecificSourceType, LocalCouldHaveMoreSpecificTypeDescriptor);
                                            }
                                        }
                                    });
                            }
                        });

                    // Track field initializations.
                    compilationContext.RegisterOperationAction(
                        (operationContext) =>
                        {
                            IFieldInitializer initializer = (IFieldInitializer)operationContext.Operation;
                            foreach (IFieldSymbol initializedField in initializer.InitializedFields)
                            {
                                AssignTo(initializedField, initializedField.Type, fieldsSourceTypes, initializer.Value);
                            }
                        },
                        OperationKind.FieldInitializer);

                    // Report fields that could have more specific types.
                    compilationContext.RegisterCompilationEndAction(
                        (compilationEndContext) =>
                        {
                            foreach (IFieldSymbol field in fieldsSourceTypes.Keys)
                            {
                                if (HasMoreSpecificSourceType(field, field.Type, fieldsSourceTypes, out var mostSpecificSourceType))
                                {
                                    Report(compilationEndContext, field, mostSpecificSourceType, FieldCouldHaveMoreSpecificTypeDescriptor);
                                }
                            }
                        });
                });
        }

        private static bool HasMoreSpecificSourceType<SymbolType>(SymbolType symbol, ITypeSymbol symbolType, Dictionary<SymbolType, HashSet<INamedTypeSymbol>> symbolsSourceTypes, out INamedTypeSymbol commonSourceType)
        {
            if (symbolsSourceTypes.TryGetValue(symbol, out var sourceTypes))
            {
                commonSourceType = CommonType(sourceTypes);
                if (commonSourceType != null && DerivesFrom(commonSourceType, (INamedTypeSymbol)symbolType))
                {
                    return true;
                }
            }

            commonSourceType = null;
            return false;
        }

        private static INamedTypeSymbol CommonType(IEnumerable<INamedTypeSymbol> types)
        {
            foreach (INamedTypeSymbol type in types)
            {
                bool success = true;
                foreach (INamedTypeSymbol testType in types)
                {
                    if (type != testType)
                    {
                        if (!DerivesFrom(testType, type))
                        {
                            success = false;
                            break;
                        }
                    }
                }

                if (success)
                {
                    return type;
                }
            }

            return null;
        }

        private static bool DerivesFrom(INamedTypeSymbol derivedType, INamedTypeSymbol baseType)
        {
            if (derivedType.TypeKind == TypeKind.Class || derivedType.TypeKind == TypeKind.Structure)
            {
                INamedTypeSymbol derivedBaseType = derivedType.BaseType;
                return derivedBaseType != null && (derivedBaseType.Equals(baseType) || DerivesFrom(derivedBaseType, baseType));
            }

            else if (derivedType.TypeKind == TypeKind.Interface)
            {
                if (derivedType.Interfaces.Contains(baseType))
                {
                    return true;
                }

                foreach (INamedTypeSymbol baseInterface in derivedType.Interfaces)
                {
                    if (DerivesFrom(baseInterface, baseType))
                    {
                        return true;
                    }
                }

                return baseType.TypeKind == TypeKind.Class && baseType.SpecialType == SpecialType.System_Object;
            }

            return false;
        }

        private static void AssignTo(IOperation target, Dictionary<ILocalSymbol, HashSet<INamedTypeSymbol>> localsSourceTypes, Dictionary<IFieldSymbol, HashSet<INamedTypeSymbol>> fieldsSourceTypes, IOperation sourceValue)
        {
            AssignTo(target, localsSourceTypes, fieldsSourceTypes, OriginalType(sourceValue));
        }

        private static void AssignTo(IOperation target, Dictionary<ILocalSymbol, HashSet<INamedTypeSymbol>> localsSourceTypes, Dictionary<IFieldSymbol, HashSet<INamedTypeSymbol>> fieldsSourceTypes, ITypeSymbol sourceType)
        {
            OperationKind targetKind = target.Kind;
            if (targetKind == OperationKind.LocalReferenceExpression)
            {
                ILocalSymbol targetLocal = ((ILocalReferenceExpression)target).Local;
                AssignTo(targetLocal, targetLocal.Type, localsSourceTypes, sourceType);
            }
            else if (targetKind == OperationKind.FieldReferenceExpression)
            {
                IFieldSymbol targetField = ((IFieldReferenceExpression)target).Field;
                AssignTo(targetField, targetField.Type, fieldsSourceTypes, sourceType);
            }
        }

        private static void AssignTo<SymbolType>(SymbolType target, ITypeSymbol targetType, Dictionary<SymbolType, HashSet<INamedTypeSymbol>> sourceTypes, IOperation sourceValue)
        {
            AssignTo(target, targetType, sourceTypes, OriginalType(sourceValue));
        }

        private static void AssignTo<SymbolType>(SymbolType target, ITypeSymbol targetType, Dictionary<SymbolType, HashSet<INamedTypeSymbol>> sourceTypes, ITypeSymbol sourceType)
        {
            if (sourceType != null && targetType != null)
            {
                TypeKind targetTypeKind = targetType.TypeKind;
                TypeKind sourceTypeKind = sourceType.TypeKind;

                // Don't suggest using an interface type instead of a class type, or vice versa.
                if ((targetTypeKind == sourceTypeKind && (targetTypeKind == TypeKind.Class || targetTypeKind == TypeKind.Interface)) ||
                    (targetTypeKind == TypeKind.Class && (sourceTypeKind == TypeKind.Structure || sourceTypeKind == TypeKind.Interface) && targetType.SpecialType == SpecialType.System_Object))
                {
                    if (!sourceTypes.TryGetValue(target, out var symbolSourceTypes))
                    {
                        symbolSourceTypes = new HashSet<INamedTypeSymbol>();
                        sourceTypes[target] = symbolSourceTypes;
                    }

                    symbolSourceTypes.Add((INamedTypeSymbol)sourceType);
                }
            }
        }

        private static ITypeSymbol OriginalType(IOperation value)
        {
            if (value.Kind == OperationKind.ConversionExpression)
            {
                IConversionExpression conversion = (IConversionExpression)value;
                if (!conversion.IsExplicitInCode)
                {
                    return conversion.Operand.Type;
                }
            }

            return value.Type;
        }

        private void Report(OperationBlockAnalysisContext context, ILocalSymbol local, ITypeSymbol moreSpecificType, DiagnosticDescriptor descriptor)
        {
            context.ReportDiagnostic(Diagnostic.Create(descriptor, local.Locations.FirstOrDefault(), local, moreSpecificType));
        }

        private void Report(CompilationAnalysisContext context, IFieldSymbol field, ITypeSymbol moreSpecificType, DiagnosticDescriptor descriptor)
        {
            context.ReportDiagnostic(Diagnostic.Create(descriptor, field.Locations.FirstOrDefault(), field, moreSpecificType));
        }
    }
}
