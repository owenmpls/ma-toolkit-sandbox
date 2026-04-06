using IngestionOrchestrator.Functions.Models;

namespace IngestionOrchestrator.Functions.Services;

public interface IEntityResolver
{
    IReadOnlyList<EntityType> Resolve(EntitySelector selector);
}

public class EntityResolver : IEntityResolver
{
    private readonly IReadOnlyList<EntityType> _entities;

    public EntityResolver(IConfigLoader configLoader)
    {
        _entities = configLoader.EntityRegistry.Entities;
    }

    public IReadOnlyList<EntityType> Resolve(EntitySelector selector)
    {
        var result = new HashSet<string>();

        // Step 1: Add all entities from included tiers
        if (selector.IncludeTiers is { Count: > 0 })
        {
            var tierSet = selector.IncludeTiers.ToHashSet();
            foreach (var entity in _entities)
            {
                if (tierSet.Contains(entity.Tier))
                    result.Add(entity.Name);
            }
        }

        // Step 2: Add explicitly included entities
        if (selector.IncludeEntities is { Count: > 0 })
        {
            foreach (var name in selector.IncludeEntities)
                result.Add(name);
        }

        // Step 3: Remove explicitly excluded entities
        if (selector.ExcludeEntities is { Count: > 0 })
        {
            foreach (var name in selector.ExcludeEntities)
                result.Remove(name);
        }

        // Return in registry order for deterministic output
        return _entities
            .Where(e => result.Contains(e.Name))
            .ToList();
    }
}
