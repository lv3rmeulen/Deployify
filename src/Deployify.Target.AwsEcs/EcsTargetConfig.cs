using System.Collections.Generic;

namespace Deployify.Target.AwsEcs;

/// <summary>
/// AWS ECS-specific settings stored under targetConfig["ecs"] in deployify-state.json.
/// </summary>
public sealed class EcsTargetConfig
{
    /// <summary>AWS region, e.g. "us-east-1".</summary>
    public string Region { get; set; } = "us-east-1";

    /// <summary>ECS cluster name. Created if it does not exist.</summary>
    public string ClusterName { get; set; } = "deployify";

    /// <summary>
    /// Whether to use AWS Fargate (serverless) or EC2 launch type.
    /// Fargate is the default — no cluster instances to manage.
    /// </summary>
    public EcsLaunchType LaunchType { get; set; } = EcsLaunchType.Fargate;

    /// <summary>
    /// VPC subnets for Fargate tasks. Required when LaunchType = Fargate.
    /// At least one subnet ID must be provided for apply to succeed.
    /// </summary>
    public List<string> SubnetIds { get; set; } = [];

    /// <summary>Security group IDs to attach to tasks.</summary>
    public List<string> SecurityGroupIds { get; set; } = [];

    /// <summary>
    /// IAM role ARN for task execution (pulling images, writing logs).
    /// If null, deployify will look for ecsTaskExecutionRole in the account.
    /// </summary>
    public string? ExecutionRoleArn { get; set; }

    /// <summary>IAM role ARN for the task itself (application permissions).</summary>
    public string? TaskRoleArn { get; set; }

    /// <summary>
    /// CloudWatch log group name. Defaults to /deployify/{ClusterName}.
    /// Created automatically if it does not exist.
    /// </summary>
    public string? LogGroupName { get; set; }

    /// <summary>Default CPU units for tasks (256 = 0.25 vCPU).</summary>
    public int DefaultCpu { get; set; } = 256;

    /// <summary>Default memory in MiB for tasks.</summary>
    public int DefaultMemory { get; set; } = 512;

    /// <summary>Default number of desired task instances per service.</summary>
    public int DefaultDesiredCount { get; set; } = 1;

    /// <summary>
    /// Whether to create an Application Load Balancer for services with HTTP bindings.
    /// When false, services are only accessible within the VPC.
    /// </summary>
    public bool CreateLoadBalancer { get; set; } = false;

    /// <summary>
    /// ARN of an existing ALB target group to register services with.
    /// Only used when CreateLoadBalancer = false and you want to attach to an existing ALB.
    /// </summary>
    public string? TargetGroupArn { get; set; }

    /// <summary>AWS profile name from ~/.aws/credentials. Null = default credential chain.</summary>
    public string? AwsProfile { get; set; }

    /// <summary>
    /// Cloud Map namespace for ECS Service Connect. When set, each service registers its
    /// HTTP port under its own name so other services reach it at http://name:port, which
    /// is how Aspire placeholders are resolved for ECS. Without it, services can't
    /// address each other by name.
    /// </summary>
    public string? ServiceConnectNamespace { get; set; }

    /// <summary>
    /// Wait for each service to reach a steady state after deploying, so apply fails
    /// when tasks crash-loop instead of reporting success. Defaults to true.
    /// </summary>
    public bool WaitForStable { get; set; } = true;

    /// <summary>Base port for services whose Aspire bindings carry no port.</summary>
    public int BasePort { get; set; } = 8080;

    /// <summary>
    /// SSM Parameter Store path under which secret env vars are stored as SecureString
    /// parameters (<c>{prefix}/{service}/{VAR}</c>). The task definition only references them,
    /// so no secret is written to the task definition file. Defaults to
    /// <c>/deployify/{ClusterName}</c>. The execution role needs <c>ssm:GetParameters</c> on
    /// this path (and <c>kms:Decrypt</c> if the parameters use a customer-managed key).
    /// </summary>
    public string? SecretsPrefix { get; set; }
}

public enum EcsLaunchType
{
    Fargate,
    Ec2
}
