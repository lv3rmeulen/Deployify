using System;
using System.Collections.Generic;
using System.Linq;
using Aspire.DashboardService.Proto.V1;
using Deployify.Core.ResourceSources;
using Google.Protobuf.WellKnownTypes;
using ProtoResourceProperty = Aspire.DashboardService.Proto.V1.ResourceProperty;

namespace Deployify.ResourceServer;

/// <summary>Turns Deployify's plain resource records into the dashboard's gRPC messages.</summary>
internal static class ProtoMapper
{
    /// <summary>The relationship type the dashboard nests resources by (KnownRelationshipTypes.Parent).</summary>
    public const string ParentRelationship = "Parent";

    public static Resource ToProto(ResourceSnapshot snapshot)
    {
        var resource = new Resource
        {
            Name = snapshot.Name,
            ResourceType = snapshot.ResourceType,
            DisplayName = snapshot.DisplayName ?? snapshot.Name,
            Uid = snapshot.Name
        };

        // Optional proto3 fields throw on null: only set what is there.
        if (snapshot.State is { } state)
        {
            resource.State = state;
        }

        if (snapshot.StateStyle is { } style)
        {
            resource.StateStyle = style.ToString().ToLowerInvariant();
        }

        if (snapshot.CreatedAt is { } created)
        {
            resource.CreatedAt = Timestamp.FromDateTimeOffset(created);
        }

        if (snapshot.StartedAt is { } started)
        {
            resource.StartedAt = Timestamp.FromDateTimeOffset(started);
        }

        if (snapshot.StoppedAt is { } stopped)
        {
            resource.StoppedAt = Timestamp.FromDateTimeOffset(stopped);
        }

        if (snapshot.Parent is { } parent)
        {
            resource.Relationships.Add(new ResourceRelationship { ResourceName = parent, Type = ParentRelationship });
        }

        foreach (var env in snapshot.Environment)
        {
            var variable = new EnvironmentVariable { Name = env.Name, IsFromSpec = true };
            if (env.Value is not null)
            {
                variable.Value = env.Value;
            }

            resource.Environment.Add(variable);
        }

        var sortOrder = 0;
        foreach (var url in snapshot.Urls)
        {
            var proto = new Url
            {
                FullUrl = url.Url,
                IsInternal = url.IsInternal,
                DisplayProperties = new UrlDisplayProperties { SortOrder = sortOrder++, DisplayName = "" }
            };
            if (url.EndpointName is not null)
            {
                proto.EndpointName = url.EndpointName;
            }

            resource.Urls.Add(proto);
        }

        foreach (var property in snapshot.Properties)
        {
            var proto = new ProtoResourceProperty
            {
                Name = property.Name,
                Value = property.Value is null ? Value.ForNull() : Value.ForString(property.Value),
                IsSensitive = property.IsSensitive
            };
            if (property.DisplayName is not null)
            {
                proto.DisplayName = property.DisplayName;
            }

            resource.Properties.Add(proto);
        }

        foreach (var health in snapshot.HealthReports)
        {
            resource.HealthReports.Add(new HealthReport
            {
                Key = health.Key,
                Status = health.Status switch
                {
                    ResourceHealthStatus.Healthy => HealthStatus.Healthy,
                    ResourceHealthStatus.Degraded => HealthStatus.Degraded,
                    _ => HealthStatus.Unhealthy
                },
                Description = health.Description ?? "",
                Exception = ""
            });
        }

        foreach (var command in snapshot.Commands)
        {
            resource.Commands.Add(ToProto(command));
        }

        return resource;
    }

    public static ResourceCommand ToProto(ResourceCommandInfo command)
    {
        var proto = new ResourceCommand
        {
            Name = command.Name,
            DisplayName = command.DisplayName,
            IsHighlighted = command.IsHighlighted,
            State = command.Enabled ? ResourceCommandState.Enabled : ResourceCommandState.Disabled
        };
        if (command.Description is not null)
        {
            proto.DisplayDescription = command.Description;
        }

        if (command.IconName is not null)
        {
            proto.IconName = command.IconName;
            proto.IconVariant = IconVariant.Regular;
        }

        if (command.ConfirmationMessage is not null)
        {
            proto.ConfirmationMessage = command.ConfirmationMessage;
        }

        return proto;
    }

    /// <summary>The distinct resource types in a set, for the dashboard's type filter.</summary>
    public static IEnumerable<ResourceType> ResourceTypes(IEnumerable<ResourceSnapshot> resources)
        => resources
            .Select(r => r.ResourceType)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Select(t => new ResourceType { UniqueName = t, DisplayName = t });

    public static WatchResourcesChange ToProto(ResourceChange change)
        => change.Upsert is { } upsert
            ? new WatchResourcesChange { Upsert = ToProto(upsert) }
            : new WatchResourcesChange
            {
                Delete = new ResourceDeletion
                {
                    ResourceName = change.Deleted!.Name,
                    ResourceType = change.Deleted.ResourceType
                }
            };
}
