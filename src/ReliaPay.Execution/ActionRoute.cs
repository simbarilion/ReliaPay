namespace ReliaPay.Execution;

/// <summary>
/// Маршрут generic HTTP-исполнителя: после <c>/api</c> ровно два сегмента, версия только в заголовке.
/// </summary>
/// <param name="Module">Первый сегмент после <c>/api</c>.</param>
/// <param name="Action">Второй сегмент после <c>/api</c>.</param>
/// <param name="ExplicitVersion">Значение <c>X-Action-Version</c> либо null, если выбирается default.</param>
public sealed record ActionRoute(string Module, string Action, int? ExplicitVersion);
