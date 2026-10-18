using K7.Server.Application.Common.Interfaces;
using K7.Server.Application.Common.Services;
using K7.Server.Domain.Entities;
using K7.Server.Domain.Entities.Metadatas;
using K7.Server.Domain.Entities.Metadatas.PersonRoles;
using Microsoft.EntityFrameworkCore;

namespace K7.Server.Application.Helpers;

/// <summary>
/// Resolves stub persons on incoming roles onto existing DB persons with batched lookups
/// (avoids N+1 queries during large serie episode-cast refreshes).
/// Also reuses the first stub of each external id / name within the same batch so one
/// SaveChanges does not insert many Person rows for the same provider id.
/// </summary>
public static class PersonRolePersonResolveHelper
{
    private const int ExternalIdChunkSize = 200;

    public static async Task ResolveAsync(
        IApplicationDbContext context,
        IEnumerable<BasePersonRole> roles,
        CancellationToken cancellationToken = default)
    {
        var roleList = roles.Where(role => role.Person is not null).ToList();
        if (roleList.Count == 0)
            return;

        var personsByExternalKey = await LoadPersonsByExternalIdsAsync(context, roleList, cancellationToken);
        var unmatchedNames = roleList
            .Where(role => FindByExternalIds(role.Person, personsByExternalKey).Count == 0)
            .Select(role => role.Person.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var personsByName = unmatchedNames.Count == 0
            ? new Dictionary<string, Person>(StringComparer.OrdinalIgnoreCase)
            : await LoadPersonsByNamesAsync(context, unmatchedNames, cancellationToken);

        foreach (var role in roleList)
        {
            var matched = FindByExternalIds(role.Person, personsByExternalKey);
            if (matched.Count == 0
                && !string.IsNullOrWhiteSpace(role.Person.Name)
                && personsByName.TryGetValue(role.Person.Name, out var byName))
            {
                matched.Add(byName);
            }

            if (matched.Count == 0)
            {
                // First time this identity appears in the batch: keep the stub and index it
                // so later roles (same guest on many episodes) reuse one Person instance.
                IndexPerson(role.Person, personsByExternalKey, personsByName);
                continue;
            }

            var existingPerson = PickCanonicalPerson(matched);
            await PersonMetadataMergeHelper.EnsurePortraitPictureLoadedAsync(
                context, existingPerson, cancellationToken);

            foreach (var duplicate in matched.Where(person => !ReferenceEquals(person, existingPerson)))
                PersonMetadataMergeHelper.MergeMissingPersonData(existingPerson, duplicate);

            if (!ReferenceEquals(existingPerson, role.Person))
                PersonMetadataMergeHelper.MergeMissingPersonData(existingPerson, role.Person);

            role.Person = existingPerson;
            IndexPerson(existingPerson, personsByExternalKey, personsByName);
        }
    }

    /// <summary>
    /// Moves role portraits onto the shared Person once so 100 episodes with the same guest
    /// queue one download instead of 100. UI already falls back to Person.PortraitPicture.
    /// </summary>
    public static void PreferPersonLevelPortraits(IEnumerable<BasePersonRole> roles)
    {
        foreach (var role in roles)
        {
            if (role.PortraitPicture is null || role.Person is null)
                continue;

            var portrait = role.PortraitPicture;
            role.PortraitPicture = null;

            if (role.Person.PortraitPicture is not null)
                continue;

            if (role.Person.Id != Guid.Empty)
                portrait.PersonId = role.Person.Id;

            role.Person.PortraitPicture = portrait;
        }
    }

    private static void IndexPerson(
        Person person,
        Dictionary<(string Provider, string Value), List<Person>> personsByExternalKey,
        Dictionary<string, Person> personsByName)
    {
        foreach (var externalId in person.ExternalIds)
        {
            if (string.IsNullOrWhiteSpace(externalId.ProviderName) || string.IsNullOrWhiteSpace(externalId.Value))
                continue;

            var key = (NormalizeProvider(externalId.ProviderName), externalId.Value);
            if (!personsByExternalKey.TryGetValue(key, out var list))
            {
                list = [];
                personsByExternalKey[key] = list;
            }

            if (!ContainsPerson(list, person))
                list.Add(person);
        }

        if (!string.IsNullOrWhiteSpace(person.Name))
            personsByName.TryAdd(person.Name, person);
    }

    private static async Task<Dictionary<(string Provider, string Value), List<Person>>> LoadPersonsByExternalIdsAsync(
        IApplicationDbContext context,
        IReadOnlyList<BasePersonRole> roles,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<(string Provider, string Value), List<Person>>();
        var pairs = roles
            .SelectMany(role => role.Person.ExternalIds)
            .Where(id => !string.IsNullOrWhiteSpace(id.ProviderName) && !string.IsNullOrWhiteSpace(id.Value))
            .Select(id => (Provider: NormalizeProvider(id.ProviderName!), Value: id.Value!))
            .Distinct()
            .GroupBy(pair => pair.Provider, StringComparer.OrdinalIgnoreCase);

        foreach (var providerGroup in pairs)
        {
            var values = providerGroup.Select(pair => pair.Value).Distinct(StringComparer.Ordinal).ToList();
            for (var offset = 0; offset < values.Count; offset += ExternalIdChunkSize)
            {
                var chunk = values.Skip(offset).Take(ExternalIdChunkSize).ToList();
                var providerName = providerGroup.Key;
                var found = await context.Persons
                    .Include(person => person.ExternalIds)
                    .Include(person => person.PortraitPicture)
                    .Where(person => person.ExternalIds.Any(externalId =>
                        externalId.ProviderName!.ToLower() == providerName
                        && chunk.Contains(externalId.Value)))
                    .ToListAsync(cancellationToken);

                foreach (var person in found)
                {
                    foreach (var externalId in person.ExternalIds)
                    {
                        if (string.IsNullOrWhiteSpace(externalId.ProviderName)
                            || string.IsNullOrWhiteSpace(externalId.Value)
                            || !string.Equals(externalId.ProviderName, providerName, StringComparison.OrdinalIgnoreCase)
                            || !chunk.Contains(externalId.Value))
                        {
                            continue;
                        }

                        var key = (NormalizeProvider(externalId.ProviderName), externalId.Value);
                        if (!result.TryGetValue(key, out var list))
                        {
                            list = [];
                            result[key] = list;
                        }

                        if (!ContainsPerson(list, person))
                            list.Add(person);
                    }
                }
            }
        }

        return result;
    }

    private static async Task<Dictionary<string, Person>> LoadPersonsByNamesAsync(
        IApplicationDbContext context,
        IReadOnlyList<string> names,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, Person>(StringComparer.OrdinalIgnoreCase);
        for (var offset = 0; offset < names.Count; offset += ExternalIdChunkSize)
        {
            var chunk = names.Skip(offset).Take(ExternalIdChunkSize).ToList();
            var found = await context.Persons
                .Include(person => person.ExternalIds)
                .Include(person => person.PortraitPicture)
                .Where(person => chunk.Contains(person.Name))
                .ToListAsync(cancellationToken);

            foreach (var person in found)
                result.TryAdd(person.Name, person);
        }

        return result;
    }

    private static List<Person> FindByExternalIds(
        Person stub,
        IReadOnlyDictionary<(string Provider, string Value), List<Person>> personsByExternalKey)
    {
        var matched = new List<Person>();
        foreach (var externalId in stub.ExternalIds)
        {
            if (string.IsNullOrWhiteSpace(externalId.ProviderName) || string.IsNullOrWhiteSpace(externalId.Value))
                continue;

            if (!personsByExternalKey.TryGetValue(
                    (NormalizeProvider(externalId.ProviderName), externalId.Value),
                    out var hits))
                continue;

            foreach (var hit in hits)
            {
                if (!ContainsPerson(matched, hit))
                    matched.Add(hit);
            }
        }

        return matched;
    }

    private static bool ContainsPerson(IReadOnlyList<Person> list, Person person)
    {
        foreach (var existing in list)
        {
            if (ReferenceEquals(existing, person))
                return true;

            if (existing.Id != Guid.Empty && existing.Id == person.Id)
                return true;
        }

        return false;
    }

    private static string NormalizeProvider(string providerName) => providerName.Trim().ToLowerInvariant();

    private static Person PickCanonicalPerson(IReadOnlyList<Person> persons) =>
        persons
            .OrderByDescending(person => person.Id != Guid.Empty)
            .ThenByDescending(person => person.ExternalIds.Any(e =>
                string.Equals(e.ProviderName, "tmdb", StringComparison.OrdinalIgnoreCase)))
            .ThenByDescending(person => !string.IsNullOrWhiteSpace(person.Biography))
            .ThenByDescending(person => person.Birthday.HasValue)
            .ThenBy(person => person.Id)
            .First();
}
