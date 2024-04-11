﻿using System.Runtime.CompilerServices;
using AIDotNet.Abstractions;
using AIDotNet.Abstractions.Dto;
using Sdcb.DashScope;
using Sdcb.DashScope.TextGeneration;

namespace AIDotNet.Qiansail
{
    public sealed class QiansailService : IApiChatCompletionService
    {
        public async Task<OpenAIResultDto> CompleteChatAsync(
            OpenAIChatCompletionInput<OpenAIChatCompletionRequestInput> input, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            using DashScopeClient client = new(options!.Key!);

            var result = await client.TextGeneration.Chat(input.Model,
                input.Messages.Select(x => new ChatMessage(x.Role, x.Content)).ToArray(), new ChatParameters()
                {
                    MaxTokens = input.MaxTokens,
                    Temperature = (float?)input.Temperature,
                    TopP = (float?)input.TopP,
                }, cancellationToken);

            return new OpenAIResultDto()
            {
                Model = input.Model,
                Choices = new[]
                {
                    new OpenAIChoiceDto()
                    {
                        Delta = new OpenAIMessageDto()
                        {
                            Content = result.Output.Text,
                            Role = "assistant"
                        }
                    }
                }
            };
        }

        public async IAsyncEnumerable<OpenAIResultDto> StreamChatAsync(
            OpenAIChatCompletionInput<OpenAIChatCompletionRequestInput> input, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            using DashScopeClient client = new(options!.Key!);

            if (input.TopP >= 1)
            {
                input.TopP = 0.9;
            }
            else if (input.TopP <= 0)
            {
                input.TopP = 0.1;
            }

            await foreach (var item in client.TextGeneration.ChatStreamed(input.Model,
                               input.Messages.Select(x => new ChatMessage(x.Role, x.Content)).ToArray(),
                               new ChatParameters()
                               {
                                   MaxTokens = input.MaxTokens,
                                   Temperature = (float?)input.Temperature,
                                   TopP = (float?)input.TopP,
                               }, cancellationToken))
            {
                yield return new OpenAIResultDto()
                {
                    Model = input.Model,
                    Choices = new[]
                    {
                        new OpenAIChoiceDto()
                        {
                            Delta = new OpenAIMessageDto()
                            {
                                Content = item.Output.Text,
                                Role = "assistant"
                            }
                        }
                    }
                };
            }
        }

        public Task<OpenAIResultDto> FunctionCompleteChatAsync(OpenAIToolsFunctionInput<OpenAIChatCompletionRequestInput> input, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public Task<OpenAIResultDto> ImageCompleteChatAsync(OpenAIChatCompletionInput<OpenAIChatVisionCompletionRequestInput> input, ChatOptions options,
            CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public IAsyncEnumerable<OpenAIResultDto> ImageStreamChatAsync(OpenAIChatCompletionInput<OpenAIChatVisionCompletionRequestInput> input, ChatOptions options,
            CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }
    }
}