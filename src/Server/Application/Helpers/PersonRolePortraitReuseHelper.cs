using K7.Server.Domain.Entities;
using K7.Server.Domain.Entities.Metadatas.PersonRoles;

namespace K7.Server.Application.Helpers;

/// <summary>
/// Reuses already-downloaded person-role portraits across metadata refreshes when the
/// remote URI (or person provider id) is unchanged, so cast refresh does not wipe artwork.
/// </summary>
public static class PersonRolePortraitReuseHelper
{
    public static void PreserveDownloadedPortraits(
        IEnumerable<BasePersonRole> existingRoles,
        IEnumerable<BasePersonRole> incomingRoles)
    {
        var reusable = new Dictionary<string, MetadataPicture>(StringComparer.OrdinalIgnoreCase);

        foreach (var role in existingRoles)
        {
            var picture = role.PortraitPicture;
            if (picture is null
                || string.IsNullOrWhiteSpace(picture.LocalPath)
                || !File.Exists(picture.LocalPath)
                || picture.OriginalRemoteUri is null)
            {
                continue;
            }

            foreach (var key in BuildKeys(role, picture.OriginalRemoteUri))
                reusable.TryAdd(key, picture);
        }

        if (reusable.Count == 0)
            return;

        var claimed = new HashSet<Guid>();

        foreach (var role in incomingRoles)
        {
            var incomingUri = role.PortraitPicture?.OriginalRemoteUri;
            if (incomingUri is null)
                continue;

            MetadataPicture? keep = null;
            foreach (var key in BuildKeys(role, incomingUri))
            {
                if (reusable.TryGetValue(key, out keep))
                    break;
            }

            if (keep is null || !claimed.Add(keep.Id))
                continue;

            // Drop the freshly created remote-only picture (never tracked) and keep local art.
            role.PortraitPicture = keep;
        }

        foreach (var role in existingRoles)
        {
            if (role.PortraitPicture is not null && claimed.Contains(role.PortraitPicture.Id))
                role.PortraitPicture = null;
        }
    }

    private static IEnumerable<string> BuildKeys(BasePersonRole role, Uri remoteUri)
    {
        var uri = remoteUri.AbsoluteUri;
        yield return "uri:" + uri;

        if (role.Person?.ExternalIds is null)
            yield break;

        foreach (var externalId in role.Person.ExternalIds)
        {
            if (string.IsNullOrWhiteSpace(externalId.ProviderName) || string.IsNullOrWhiteSpace(externalId.Value))
                continue;

            yield return externalId.ProviderName + ":" + externalId.Value + "|" + uri;
        }
    }
}
