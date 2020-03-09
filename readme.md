# Pulumi Azure - TODO list sample app

This repository is a mono-repository that contains an example of a web application that can be deployed into Azure using Pulumi CLI.

It uses several Azure features such as:

* Azure SQL Server
* Azure Functions (as a .NET Core Web API)
* Azure Static Websites (hosted inside a Storage Account)
* Azure App Insights

### How to deploy?

#### Install Pulumi

Follow the instructions to install Pulumi CLI and the `az` CLI. https://www.pulumi.com/docs/get-started/azure/install-pulumi/

Don't forget to login using commands `pulumi login` and `az login`.
You will also need .NET Core 3.1 and node.js.

#### Build projects

1. Build the frontend project 

Start a new cli and run: 

```
cd Front && yarn install && yarn build
```

2. Build the backend project

Start a new cli and run: 

```
cd TodoFunctions && dotnet publish
```

#### Run pulumi

Once the previous projects are built, start a new cli and run: 

```
cd Infrastructure/TodoAppInfrastructure
pulumi up
```

If it is your first deployment of the project, you will be ask to create a new stack. So, create a new stack named `dev`.

Then, your new infrastructure will be created in Azure.