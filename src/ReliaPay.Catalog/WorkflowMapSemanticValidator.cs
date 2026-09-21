using System.Text.Json;
using ReliaPay.Contracts;
using ReliaPay.Json;

namespace ReliaPay.Catalog;

/// <summary>
/// Семантика карты: уникальность, достижимость, ацикличность, покрытие outcome,
/// политика action, retry и непересечение JSON Pointer.
/// </summary>
public sealed class WorkflowMapSemanticValidator
{
    /// <summary>
    /// Проверяет карту относительно опубликованного каталога action.
    /// </summary>
    /// <returns>Null при успехе, иначе код и сообщение.</returns>
    public async Task<(string Code, string Message)?> ValidateAsync(
        WorkflowMap map,
        IPublishedActionLookup lookup,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(lookup);

        Dictionary<string, WorkflowMapStep> steps = new(StringComparer.Ordinal);
        foreach (WorkflowMapStep step in map.Steps)
        {
            if (!steps.TryAdd(step.Key, step))
            {
                return Fail("step keys must be unique");
            }
        }

        if (!steps.ContainsKey(map.StartStep))
        {
            return Fail("start_step must name an existing step");
        }

        if (map.Steps.All(static step => step.Type != "end"))
        {
            return Fail("map must contain an end step");
        }

        HashSet<(string From, string Outcome)> seenOutcomes = [];
        Dictionary<string, List<string>> outgoing = new(StringComparer.Ordinal);
        Dictionary<string, List<string>> adjacency = new(StringComparer.Ordinal);
        foreach (WorkflowMapTransition transition in map.Transitions)
        {
            if (!steps.ContainsKey(transition.From) || !steps.ContainsKey(transition.To))
            {
                return Fail("transition references an unknown step");
            }

            if (steps[transition.From].Type == "end")
            {
                return Fail("end step cannot have outgoing transitions");
            }

            if (!seenOutcomes.Add((transition.From, transition.Outcome)))
            {
                return Fail("transition outcomes from a step must be unique");
            }

            Add(outgoing, transition.From, transition.Outcome);
            Add(adjacency, transition.From, transition.To);
        }

        if (HasCycle(map.StartStep, adjacency, steps.Keys))
        {
            return Fail("map must be acyclic");
        }

        HashSet<string> reachable = Reachable(map.StartStep, adjacency);
        foreach (string key in steps.Keys)
        {
            if (!reachable.Contains(key))
            {
                return Fail("every step must be reachable from start_step");
            }
        }

        foreach (WorkflowMapStep step in map.Steps)
        {
            if (step.Type == "end")
            {
                continue;
            }

            if (!outgoing.ContainsKey(step.Key))
            {
                return Fail("non-end step must have outgoing transitions");
            }
        }

        foreach (WorkflowMapStep step in map.Steps)
        {
            HashSet<string> actual = outgoing.TryGetValue(step.Key, out List<string>? values)
                ? values.ToHashSet(StringComparer.Ordinal)
                : [];

            if (step.Type == "automatic" && step.Task is not null)
            {
                (string Code, string Message)? taskError = await ValidateAutomaticAsync(
                        step.Task,
                        actual,
                        lookup,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (taskError is not null)
                {
                    return taskError;
                }
            }
            else if (step.Type == "wait_signal")
            {
                if (actual.Count != 1 || !actual.Contains(step.Outcome ?? string.Empty))
                {
                    return Fail("wait_signal step must map exactly its outcome");
                }
            }
            else if (step.Type == "manual")
            {
                HashSet<string> allowed = (step.AllowedOutcomes ?? []).ToHashSet(StringComparer.Ordinal);
                if (!actual.SetEquals(allowed))
                {
                    return Fail("manual step transitions must match allowed_outcomes");
                }
            }
        }

        return null;
    }

    private static async Task<(string Code, string Message)?> ValidateAutomaticAsync(
        WorkflowMapTask task,
        HashSet<string> actualOutcomes,
        IPublishedActionLookup lookup,
        CancellationToken cancellationToken)
    {
        if (task.DelaysMs.Count != task.MaxAttempts - 1)
        {
            return Fail("delays_ms length must equal max_attempts minus one");
        }

        if (HasOverlappingDestinations(task))
        {
            return Fail("input mapping destinations must not overlap");
        }

        PublishedActionSnapshot? published = await lookup
            .FindAsync(task.Module, task.Action, task.ActionVersion, cancellationToken)
            .ConfigureAwait(false);
        if (published is null)
        {
            return Fail("automatic step action version is not published");
        }

        if (!published.Enabled)
        {
            return Fail("automatic step action version is disabled");
        }

        HashSet<string> catalogPolicy = published.RequiredPolicy.ToHashSet(StringComparer.Ordinal);
        HashSet<string> mapPolicy = task.RequiredPolicy.ToHashSet(StringComparer.Ordinal);
        if (!catalogPolicy.SetEquals(mapPolicy))
        {
            return Fail("required_policy must equal the published action policy");
        }

        HashSet<string> catalogOutcomes = published.Outcomes.ToHashSet(StringComparer.Ordinal);
        if (!catalogOutcomes.SetEquals(actualOutcomes))
        {
            return Fail("automatic transitions must cover action outcomes exactly");
        }

        return null;
    }

    private static bool HasOverlappingDestinations(WorkflowMapTask task)
    {
        List<string> destinations = [.. task.InputMapping.Keys];
        if (task.InputConstants.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in task.InputConstants.EnumerateObject())
            {
                destinations.Add(JsonPointer.FromPropertyName(property.Name));
            }
        }

        for (int left = 0; left < destinations.Count; left++)
        {
            for (int right = left + 1; right < destinations.Count; right++)
            {
                if (JsonPointer.DestinationsOverlap(destinations[left], destinations[right]))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static void Add(Dictionary<string, List<string>> map, string key, string value)
    {
        if (!map.TryGetValue(key, out List<string>? list))
        {
            list = [];
            map[key] = list;
        }

        list.Add(value);
    }

    private static HashSet<string> Reachable(string start, Dictionary<string, List<string>> adjacency)
    {
        HashSet<string> seen = new(StringComparer.Ordinal) { start };
        Queue<string> queue = new();
        queue.Enqueue(start);
        while (queue.Count > 0)
        {
            string current = queue.Dequeue();
            if (!adjacency.TryGetValue(current, out List<string>? next))
            {
                continue;
            }

            foreach (string child in next)
            {
                if (seen.Add(child))
                {
                    queue.Enqueue(child);
                }
            }
        }

        return seen;
    }

    private static bool HasCycle(string start, Dictionary<string, List<string>> adjacency, IEnumerable<string> keys)
    {
        Dictionary<string, int> color = new(StringComparer.Ordinal);
        foreach (string key in keys)
        {
            color[key] = 0;
        }

        return Visit(start);

        bool Visit(string node)
        {
            color[node] = 1;
            if (adjacency.TryGetValue(node, out List<string>? next))
            {
                foreach (string child in next)
                {
                    if (color[child] == 1)
                    {
                        return true;
                    }

                    if (color[child] == 0 && Visit(child))
                    {
                        return true;
                    }
                }
            }

            color[node] = 2;
            return false;
        }
    }

    private static (string Code, string Message) Fail(string message)
    {
        return (ErrorCodes.RequestInvalid, message);
    }
}
