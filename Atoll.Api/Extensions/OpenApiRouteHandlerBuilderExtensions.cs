using Microsoft.OpenApi;

namespace Atoll.Api.Extensions;

public static class OpenApiRouteHandlerBuilderExtensions
{
    /// <summary>
    ///     Documents JSON response variants that share a status code. ASP.NET Core 10 otherwise
    ///     keeps only the last response type; native same-status composition arrives in .NET 11.
    /// </summary>
    public static RouteHandlerBuilder ProducesJsonOneOf<TFirst, TSecond>(
        this RouteHandlerBuilder builder,
        int statusCode = StatusCodes.Status200OK)
    {
        return builder.AddOpenApiOperationTransformer(async (operation, context, cancellationToken) =>
        {
            IOpenApiSchema firstSchema = await context.GetOrCreateSchemaAsync(
                typeof(TFirst), null, cancellationToken);
            IOpenApiSchema secondSchema = await context.GetOrCreateSchemaAsync(
                typeof(TSecond), null, cancellationToken);

            if (context.Document is not null)
            {
                var firstSchemaId = SchemaId(typeof(TFirst));
                var secondSchemaId = SchemaId(typeof(TSecond));

                context.Document.AddComponent(firstSchemaId, firstSchema);
                firstSchema = new OpenApiSchemaReference(firstSchemaId, context.Document);

                context.Document.AddComponent(secondSchemaId, secondSchema);
                secondSchema = new OpenApiSchemaReference(secondSchemaId, context.Document);
            }

            var response = operation.Responses![statusCode.ToString()];
            response.Content!["application/json"].Schema = new OpenApiSchema
            {
                OneOf = [firstSchema, secondSchema]
            };
        });
    }

    private static string SchemaId(Type type)
    {
        if (!type.IsGenericType)
            return type.Name;

        var name = type.Name[..type.Name.IndexOf('`')];
        return $"{name}Of{string.Concat(type.GetGenericArguments().Select(SchemaId))}";
    }
}