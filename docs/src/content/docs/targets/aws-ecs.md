---
title: "AWS ECS (Fargate)"
description: "Deploying an Aspire app to AWS ECS (Fargate) with Deployify."
---

Target ID: `AwsEcs`

Generates ECS task definition JSON files and registers/updates services via the AWS CLI.

```json
"targetConfig": {
  "ecs": {
    "region": "us-east-1",
    "clusterName": "my-cluster",
    "launchType": "Fargate",
    "subnetIds": ["subnet-abc123"],
    "securityGroupIds": ["sg-abc123"],
    "executionRoleArn": "arn:aws:iam::123456789:role/ecsTaskExecutionRole",
    "taskRoleArn": "arn:aws:iam::123456789:role/ecsTaskRole",
    "logGroupName": "/ecs/myapp",
    "defaultCpu": 256,
    "defaultMemory": 512,
    "awsProfile": "default",
    "serviceConnectNamespace": "myapp.local",
    "waitForStable": true
  }
}
```

- `serviceConnectNamespace` enables ECS Service Connect so services reach each other by name.
  Without it, inter-service URLs can't be resolved on ECS.
- `waitForStable` (default `true`) makes `apply` wait until each service reaches a steady
  state, so a task that crash-loops is reported as a failure instead of a success.
- The CloudWatch log group is created automatically (`awslogs-create-group`); the execution
  role needs `logs:CreateLogGroup`.

Init flags: `--ecs-region`, `--ecs-cluster`, `--ecs-profile`, `--ecs-subnets`, `--ecs-security-groups`, `--ecs-execution-role`

Requires the [AWS CLI](https://aws.amazon.com/cli/) on `PATH`.
