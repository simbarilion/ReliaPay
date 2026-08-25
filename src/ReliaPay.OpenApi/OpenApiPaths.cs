namespace ReliaPay.OpenApi;

/// <summary>
/// Канонические пути JSON OpenAPI-документов, которые публикует внутренний api и пропускает gateway.
/// Swagger UI эти пути не заменяет.
/// </summary>
public static class OpenApiPaths
{
    /// <summary>
    /// Документ только включённых default-версий всех маршрутов.
    /// </summary>
    public const string DefaultDocument = "/openapi/default.json";

    /// <summary>
    /// Префикс документа одной точной версии action.
    /// </summary>
    public const string ActionDocumentPrefix = "/openapi/actions/";
}
