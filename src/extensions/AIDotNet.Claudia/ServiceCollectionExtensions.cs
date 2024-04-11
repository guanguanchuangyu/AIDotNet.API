﻿using AIDotNet.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace AIDotNet.Claudia
{
    public static class ServiceCollectionExtensions
    {
        public static IServiceCollection AddClaudia(this IServiceCollection services)
        {
            IApiChatCompletionService.ServiceNames.Add("Claudia", ClaudiaOptions.ServiceName);
            return services;
        }
    }
}