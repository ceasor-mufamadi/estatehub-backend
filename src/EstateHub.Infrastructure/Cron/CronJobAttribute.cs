using System;

namespace EstateHub.Infrastructure.Cron;

[AttributeUsage(AttributeTargets.Class)]
public class CronJobAttribute : Attribute
{
    public string Name { get; }

    public CronJobAttribute(string name)
    {
        Name = name;
    }
}