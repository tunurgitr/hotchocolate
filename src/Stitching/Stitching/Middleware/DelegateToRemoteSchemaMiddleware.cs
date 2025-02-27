using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using HotChocolate.Execution;
using HotChocolate.Language;
using HotChocolate.Resolvers;
using HotChocolate.Stitching.Delegation;
using HotChocolate.Stitching.Properties;
using HotChocolate.Stitching.Utilities;
using HotChocolate.Types;
using HotChocolate.Utilities;

namespace HotChocolate.Stitching
{
    public class DelegateToRemoteSchemaMiddleware
    {
        private const string _remoteErrorField = "remote";
        private static readonly RootScopedVariableResolver _resolvers =
            new RootScopedVariableResolver();
        private readonly FieldDelegate _next;

        public DelegateToRemoteSchemaMiddleware(FieldDelegate next)
        {
            _next = next ?? throw new ArgumentNullException(nameof(next));
        }

        public async Task InvokeAsync(IMiddlewareContext context)
        {
            DelegateDirective delegateDirective = context.Field
                .Directives[DirectiveNames.Delegate]
                .FirstOrDefault()?.ToObject<DelegateDirective>();

            if (delegateDirective != null)
            {
                IImmutableStack<SelectionPathComponent> path =
                    delegateDirective.Path is null
                    ? ImmutableStack<SelectionPathComponent>.Empty
                    : SelectionPathParser.Parse(delegateDirective.Path);

                IReadOnlyQueryRequest request =
                    CreateQuery(context, delegateDirective.Schema, path);

                IReadOnlyQueryResult result = await ExecuteQueryAsync(
                    context, request, delegateDirective.Schema)
                    .ConfigureAwait(false);

                UpdateContextData(context, result, delegateDirective);

                context.Result = new SerializedData(
                    ExtractData(result.Data, path.Count()));
                ReportErrors(context, result.Errors);
            }

            await _next.Invoke(context).ConfigureAwait(false);
        }

        private void UpdateContextData(
            IResolverContext context,
            IReadOnlyQueryResult result,
            DelegateDirective delegateDirective)
        {
            if (result.ContextData.Count > 0)
            {
                ImmutableDictionary<string, object>.Builder builder =
                    ImmutableDictionary.CreateBuilder<string, object>();
                builder.AddRange(context.ScopedContextData);
                builder[WellKnownProperties.SchemaName] =
                    delegateDirective.Schema;
                builder.AddRange(result.ContextData);
                context.ScopedContextData = builder.ToImmutableDictionary();
            }
            else
            {
                context.ScopedContextData =
                    context.ScopedContextData.SetItem(
                        WellKnownProperties.SchemaName,
                        delegateDirective.Schema);
            }
        }

        private static IReadOnlyQueryRequest CreateQuery(
            IMiddlewareContext context,
            NameString schemaName,
            IImmutableStack<SelectionPathComponent> path)
        {
            var fieldRewriter = new ExtractFieldQuerySyntaxRewriter(
                context.Schema,
                context.Service<IEnumerable<IQueryDelegationRewriter>>());

            OperationType operationType =
                context.Schema.IsRootType(context.ObjectType)
                    ? context.Operation.Operation
                    : OperationType.Query;

            ExtractedField extractedField = fieldRewriter.ExtractField(
                schemaName, context.Document, context.Operation,
                context.FieldSelection, context.ObjectType);

            IEnumerable<VariableValue> scopedVariables =
                ResolveScopedVariables(
                    context, schemaName, operationType, path);

            IReadOnlyCollection<VariableValue> variableValues =
                CreateVariableValues(
                    context, scopedVariables, extractedField);

            DocumentNode query = RemoteQueryBuilder.New()
                .SetOperation(operationType)
                .SetSelectionPath(path)
                .SetRequestField(extractedField.Field)
                .AddVariables(CreateVariableDefs(variableValues))
                .AddFragmentDefinitions(extractedField.Fragments)
                .Build();

            var requestBuilder = QueryRequestBuilder.New();

            AddVariables(context, schemaName,
                requestBuilder, query, variableValues);

            requestBuilder.SetQuery(query);
            requestBuilder.AddProperty(
                WellKnownProperties.IsAutoGenerated,
                true);

            return requestBuilder.Create();
        }

        private static async Task<IReadOnlyQueryResult> ExecuteQueryAsync(
            IResolverContext context,
            IReadOnlyQueryRequest request,
            string schemaName)
        {
            IRemoteQueryClient remoteQueryClient =
                context.Service<IStitchingContext>()
                    .GetRemoteQueryClient(schemaName);

            IExecutionResult result = await remoteQueryClient
                    .ExecuteAsync(request)
                    .ConfigureAwait(false);

            if (result is IReadOnlyQueryResult queryResult)
            {
                return queryResult;
            }

            throw new QueryException(
                StitchingResources.DelegationMiddleware_OnlyQueryResults);
        }

        private static object ExtractData(
            IReadOnlyDictionary<string, object> data,
            int levels)
        {
            if (data.Count == 0)
            {
                return null;
            }

            object obj = data.Count == 0 ? null : data.First().Value;

            if (obj != null && levels > 1)
            {
                for (int i = levels - 1; i >= 1; i--)
                {
                    var current = obj as IReadOnlyDictionary<string, object>;
                    obj = current.Count == 0 ? null : current.First().Value;
                    if (obj is null)
                    {
                        return null;
                    }
                }
            }

            return obj;
        }

        private static void ReportErrors(
            IResolverContext context,
            IEnumerable<IError> errors)
        {
            foreach (IError error in errors)
            {
                IErrorBuilder builder = ErrorBuilder.FromError(error)
                    .SetExtension(_remoteErrorField, error.RemoveException());

                if (error.Path != null)
                {
                    Path path = RewriteErrorPath(error, context.Path);
                    builder.SetPath(path)
                        .ClearLocations()
                        .AddLocation(context.FieldSelection);
                }

                context.ReportError(builder.Build());
            }
        }

        private static Path RewriteErrorPath(IError error, Path path)
        {
            Path current = path;

            if (error.Path.Count > 0
                && error.Path[0] is string s
                && current.Name.Equals(s))
            {
                for (int i = 1; i < error.Path.Count; i++)
                {
                    if (error.Path[i] is string name)
                    {
                        current = current.Append(name);
                    }

                    if (error.Path[i] is int index)
                    {
                        current = current.Append(index);
                    }
                }
            }

            return current;
        }

        private static IReadOnlyCollection<VariableValue> CreateVariableValues(
            IMiddlewareContext context,
            IEnumerable<VariableValue> scopedVaribles,
            ExtractedField extractedField)
        {
            var values = new Dictionary<string, VariableValue>();

            foreach (VariableValue value in scopedVaribles)
            {
                values[value.Name] = value;
            }

            IReadOnlyDictionary<string, object> requestVariables =
                context.GetVariables();

            foreach (VariableValue value in ResolveUsedRequestVariables(
                extractedField, requestVariables))
            {
                values[value.Name] = value;
            }

            return values.Values;
        }

        private static IReadOnlyList<VariableValue> ResolveScopedVariables(
            IResolverContext context,
            NameString schemaName,
            OperationType operationType,
            IEnumerable<SelectionPathComponent> components)
        {
            IStitchingContext stitchingContext =
                context.Service<IStitchingContext>();

            ISchema remoteSchema =
                stitchingContext.GetRemoteSchema(schemaName);

            IComplexOutputType type =
                remoteSchema.GetOperationType(operationType);

            var variables = new List<VariableValue>();
            SelectionPathComponent[] comps = components.Reverse().ToArray();

            for (int i = 0; i < comps.Length; i++)
            {
                SelectionPathComponent component = comps[i];

                if (!type.Fields.TryGetField(component.Name.Value,
                    out IOutputField field))
                {
                    throw new QueryException(new Error
                    {
                        Message = string.Format(
                            CultureInfo.InvariantCulture,
                            StitchingResources
                                .DelegationMiddleware_PathElementInvalid,
                            component.Name.Value,
                            type.Name)
                    });
                }

                ResolveScopedVariableArguments(
                    context, component, field, variables);

                if (i + 1 < comps.Length)
                {
                    if (!field.Type.IsComplexType())
                    {
                        throw new QueryException(new Error
                        {
                            Message = StitchingResources
                                .DelegationMiddleware_PathElementTypeUnexpected
                        });
                    }
                    type = (IComplexOutputType)field.Type.NamedType();
                }
            }

            return variables;
        }

        private static void ResolveScopedVariableArguments(
            IResolverContext context,
            SelectionPathComponent component,
            IOutputField field,
            ICollection<VariableValue> variables)
        {
            ITypeConversion typeConversion =
                context.Service<IServiceProvider>()
                    .GetTypeConversion();

            foreach (ArgumentNode argument in component.Arguments)
            {
                if (!field.Arguments.TryGetField(argument.Name.Value,
                    out IInputField arg))
                {
                    throw new QueryException(new Error
                    {
                        Message = string.Format(
                            CultureInfo.InvariantCulture,
                            StitchingResources
                                .DelegationMiddleware_ArgumentNotFound,
                            argument.Name.Value)
                    });
                }

                if (argument.Value is ScopedVariableNode sv)
                {
                    VariableValue variable =
                        _resolvers.Resolve(context, sv, arg.Type.ToTypeNode());

                    object value = variable.Value;

                    if (!arg.Type.IsInstanceOfType(value))
                    {
                        value = ConvertValue(typeConversion, arg.Type, value);
                    }

                    variable = new VariableValue
                    (
                        variable.Name,
                        variable.Type,
                        arg.Type.Serialize(value),
                        variable.DefaultValue
                    );

                    variables.Add(variable);
                }
            }
        }

        private static object ConvertValue(
            ITypeConversion converter,
            IInputType type,
            object value)
        {
            Type sourceType = typeof(object);

            if (type.IsListType() && value is IEnumerable<object> e)
            {
                if (e.Any())
                {
                    Type elementType = e.FirstOrDefault()?.GetType();
                    if (elementType != null)
                    {
                        sourceType =
                            typeof(IEnumerable<>).MakeGenericType(elementType);
                    }
                }
                else
                {
                    return Activator.CreateInstance(type.ClrType);
                }
            }

            return converter.Convert(sourceType, type.ClrType, value);
        }

        private static IEnumerable<VariableValue> ResolveUsedRequestVariables(
            ExtractedField extractedField,
            IReadOnlyDictionary<string, object> requestVariables)
        {
            foreach (VariableDefinitionNode variable in
                extractedField.Variables)
            {
                string name = variable.Variable.Name.Value;
                requestVariables.TryGetValue(name, out object value);

                yield return new VariableValue
                (
                    name,
                    variable.Type,
                    value,
                    variable.DefaultValue
                );
            }
        }

        private static void AddVariables(
            IResolverContext context,
            NameString schemaName,
            IQueryRequestBuilder builder,
            DocumentNode query,
            IEnumerable<VariableValue> variableValues)
        {
            OperationDefinitionNode operation =
                query.Definitions.OfType<OperationDefinitionNode>().First();
            var usedVariables = new HashSet<string>(
                operation.VariableDefinitions.Select(t =>
                    t.Variable.Name.Value));

            foreach (VariableValue variableValue in variableValues)
            {
                if (usedVariables.Contains(variableValue.Name))
                {
                    object value = variableValue.Value;

                    if (context.Schema.TryGetType(
                        variableValue.Type.NamedType().Name.Value,
                        out InputObjectType inputType))
                    {
                        var wrapped = WrapType(inputType, variableValue.Type);
                        value = ObjectVariableRewriter.RewriteVariable(
                            schemaName, wrapped, value);
                    }

                    builder.AddVariableValue(variableValue.Name, value);
                }
            }
        }

        private static IInputType WrapType(
            IInputType namedType,
            ITypeNode typeNode)
        {
            if (typeNode is NonNullTypeNode nntn)
            {
                return new NonNullType(WrapType(namedType, nntn.Type));
            }
            else if (typeNode is ListTypeNode ltn)
            {
                return new ListType(WrapType(namedType, ltn.Type));
            }
            else
            {
                return namedType;
            }
        }

        private static IReadOnlyList<VariableDefinitionNode> CreateVariableDefs(
            IReadOnlyCollection<VariableValue> variableValues)
        {
            var definitions = new List<VariableDefinitionNode>();

            foreach (VariableValue variableValue in variableValues)
            {
                definitions.Add(new VariableDefinitionNode(
                    null,
                    new VariableNode(new NameNode(variableValue.Name)),
                    variableValue.Type,
                    variableValue.DefaultValue,
                    Array.Empty<DirectiveNode>()
                ));
            }

            return definitions;
        }
    }
}
