﻿using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using AIDotNet.Abstractions;
using AIDotNet.Abstractions.Dto;
using AIDotNet.API.Service.Domain;
using AIDotNet.API.Service.Infrastructure.Helper;
using Claudia;
using TokenApi.Service.Exceptions;

namespace AIDotNet.API.Service.Service;

public sealed class ChatService(
    IServiceProvider serviceProvider,
    ChannelService channelService,
    TokenService tokenService,
    UserService userService,
    LoggerService loggerService)
    : ApplicationService(serviceProvider)
{
    private const string ConsumerTemplate = "模型倍率：{0} 补全倍率：{1}";

    private static readonly Dictionary<string, decimal> PromptRate = new();
    private static readonly Dictionary<string, decimal> CompletionRate = new();

    static ChatService()
    {
        if (File.Exists("prompt-rate.json"))
        {
            PromptRate = JsonSerializer.Deserialize<Dictionary<string, decimal>>(File.ReadAllText("prompt-rate.json"));

            CompletionRate = new Dictionary<string, decimal>();
        }
    }

    public async ValueTask EmbeddingAsync(HttpContext context)
    {
        var (token, user) = await tokenService.CheckTokenAsync(context);

        using var body = new MemoryStream();
        await context.Request.Body.CopyToAsync(body);

        var module = JsonSerializer.Deserialize<EmbeddingInput>(body.ToArray());

        if (module == null)
        {
            throw new Exception("模型校验异常");
        }

        // 获取渠道 通过算法计算权重
        var channel = CalculateWeight((await channelService.GetChannelsAsync())
            .Where(x => x.Models.Contains(module.Model)));

        if (channel == null)
        {
            throw new NotModelException(module.Model);
        }

        // 获取渠道指定的实现类型的服务
        var openService = GetKeyedService<IApiChatCompletionService>(channel.Type);

        if (openService == null)
        {
            await WriteEndAsync(context, "渠道服务不存在");
            return;
        }


        if (PromptRate.TryGetValue(module.Model, out var rate))
        {
            var requestToken = TokenHelper.GetTotalTokens(module.Input);


            var quota = requestToken * rate;

            var completionRatio = GetCompletionRatio(module.Model);
            quota +=  (rate * completionRatio);

            // 将quota 四舍五入
            quota = Math.Round(quota, 0, MidpointRounding.AwayFromZero);

            await loggerService.CreateConsumeAsync(string.Format(ConsumerTemplate, rate, completionRatio), module.Model,
                requestToken, 0, (int)quota, token.Name, user?.UserName, token.Creator, channel.Id,
                channel.Name);

            await userService.ConsumeAsync(user!.Id, (long)quota, requestToken, token.Key);

            await DbContext.SaveChangesAsync();
        }
    }

    public async Task CompletionsAsync(HttpContext context)
    {
        var (token, user) = await tokenService.CheckTokenAsync(context);

        using var body = new MemoryStream();
        await context.Request.Body.CopyToAsync(body);

        var module = JsonSerializer.Deserialize<OpenAICompletionInput>(body.ToArray());

        if (module == null)
        {
            throw new Exception("模型校验异常");
        }

        // 获取渠道 通过算法计算权重
        var channel = CalculateWeight((await channelService.GetChannelsAsync())
            .Where(x => x.Models.Contains(module.Model)));

        if (channel == null)
        {
            throw new NotModelException(module.Model);
        }

        // 获取渠道指定的实现类型的服务
        var openService = GetKeyedService<IApiChatCompletionService>(channel.Type);

        if (openService == null)
        {
            await WriteEndAsync(context, "渠道服务不存在");
            return;
        }

        if (PromptRate.TryGetValue(module.Model, out var rate))
        {
            int requestToken;
            int responseToken = 0;

            var tools =
                JsonSerializer.Deserialize<OpenAIToolsFunctionInput<OpenAIChatCompletionRequestInput>>(body.ToArray());

            if (tools != null && !string.IsNullOrEmpty(tools.ToolChoice))
            {
                (requestToken, responseToken) = await ToolChoice(context, body, channel, openService);
            }
            else if (module.Stream == true)
            {
                (requestToken, responseToken) = await StreamHandlerAsync(context, body, module, channel, openService);
            }
            else
            {
                (requestToken, responseToken) = await ChatHandlerAsync(context, body, module, channel, openService);
            }

            var quota = requestToken * rate;

            var completionRatio = GetCompletionRatio(module.Model);
            quota += responseToken * (rate * completionRatio);

            // 将quota 四舍五入
            quota = Math.Round(quota, 0, MidpointRounding.AwayFromZero);

            await loggerService.CreateConsumeAsync(string.Format(ConsumerTemplate, rate, completionRatio), module.Model,
                requestToken, responseToken, (int)quota, token.Name, user?.UserName, token.Creator, channel.Id,
                channel.Name);

            await userService.ConsumeAsync(user!.Id, (long)quota, requestToken, token.Key);

            await DbContext.SaveChangesAsync();
        }
    }

    private static async ValueTask<(int, int)> ChatHandlerAsync(HttpContext context, MemoryStream body,
        OpenAICompletionInput module, ChatChannel channel, IApiChatCompletionService openService)
    {
        int requestToken;
        int responseToken = 0;
        var responseMessage = new StringBuilder();

        var setting = new ChatOptions()
        {
            Key = channel.Key,
            Address = channel.Address,
        };

        if (module.Model.Contains("vision"))
        {
            requestToken = 0;

            var message =
                JsonSerializer.Deserialize<OpenAIChatCompletionInput<OpenAIChatVisionCompletionRequestInput>>(
                    body.ToArray());

            requestToken = TokenHelper.GetTotalTokens(message?.Messages.SelectMany(x => x.content).Where(x => x.type == "text").Select(x => x.text).ToArray());

            var result = await openService.ImageCompleteChatAsync(message, setting);

            await context.Response.WriteAsJsonAsync(result);

            responseToken = TokenHelper.GetTokens(responseMessage.ToString());
        }
        else
        {
            var message = JsonSerializer.Deserialize<OpenAIChatCompletionInput<OpenAIChatCompletionRequestInput>>(
                body.ToArray());

            requestToken = TokenHelper.GetTotalTokens(message?.Messages.Select(x => x.Content).ToArray());


            var result = await openService.CompleteChatAsync(message, setting);

            await context.Response.WriteAsJsonAsync(result);

            responseToken = TokenHelper.GetTokens(responseMessage.ToString());
        }

        return (requestToken, responseToken);
    }

    /// <summary>
    /// ToolChoice 处理
    /// </summary>
    /// <param Name="context"></param>
    /// <param Name="body"></param>
    /// <param Name="channel"></param>
    /// <param Name="openService"></param>
    /// <returns></returns>
    private static async ValueTask<(int, int)> ToolChoice(HttpContext context, MemoryStream body,
        ChatChannel channel, IApiChatCompletionService openService)
    {
        int responseToken = 0;

        var message = JsonSerializer.Deserialize<OpenAIToolsFunctionInput<OpenAIChatCompletionRequestInput>>(
            body.ToArray());

        var requestToken = TokenHelper.GetTotalTokens(message?.Messages.Select(x => x.Content).ToArray());

        var setting = new ChatOptions()
        {
            Key = channel.Key,
            Address = channel.Address,
        };

        var result = await openService.FunctionCompleteChatAsync(message, setting);

        await context.Response.WriteAsJsonAsync(result);

        return (requestToken, responseToken);
    }

    /// <summary>
    /// Stream 对话处理
    /// </summary>
    /// <param Name="context"></param>
    /// <param Name="body"></param>
    /// <param Name="module"></param>
    /// <param Name="channel"></param>
    /// <param Name="openService"></param>
    /// <returns></returns>
    private static async ValueTask<(int, int)> StreamHandlerAsync(HttpContext context, MemoryStream body,
        OpenAICompletionInput module, ChatChannel channel, IApiChatCompletionService openService)
    {
        int requestToken;
        int responseToken = 0;

        var setting = new ChatOptions()
        {
            Key = channel.Key,
            Address = channel.Address,
        };

        var id = "chatcmpl-" + StringHelper.GenerateRandomString(29);
        var systemFingerprint = "fp_" + StringHelper.GenerateRandomString(10);
        var responseMessage = new StringBuilder();

        if (module.Model.Contains("vision"))
        {
            requestToken = 0;

            var message =
                JsonSerializer.Deserialize<OpenAIChatCompletionInput<OpenAIChatVisionCompletionRequestInput>>(
                    body.ToArray());

            requestToken = TokenHelper.GetTotalTokens(message?.Messages.SelectMany(x => x.content).Where(x => x.type == "text").Select(x => x.text).ToArray());

            await foreach (var item in openService.ImageStreamChatAsync(message, setting))
            {
                responseMessage.Append(item);
                await WriteOpenAiResultAsync(context, item.Choices.FirstOrDefault()?.Delta.Content ?? string.Empty, module.Model,
                    systemFingerprint, id);
            }

            await WriteEndAsync(context);
        }
        else
        {
            var message = JsonSerializer.Deserialize<OpenAIChatCompletionInput<OpenAIChatCompletionRequestInput>>(
                body.ToArray());

            requestToken = TokenHelper.GetTotalTokens(message?.Messages.Select(x => x.Content).ToArray());

            await foreach (var item in openService.StreamChatAsync(message, setting))
            {
                responseMessage.Append(item);
                await WriteOpenAiResultAsync(context, item.Choices.FirstOrDefault()?.Delta.Content ?? string.Empty, module.Model,
                    systemFingerprint, id);
            }

            await WriteEndAsync(context);

        }

        responseToken = TokenHelper.GetTokens(responseMessage.ToString());

        return (requestToken, responseToken);
    }

    private static async ValueTask WriteOpenAiResultAsync(HttpContext context, string content, string model,
        string systemFingerprint, string id)
    {
        var openAiResult = new OpenAIResultDto()
        {
            Id = id,
            _object = "chat.completion.chunk",
            Created = DateTimeOffset.Now.ToUnixTimeSeconds(),
            Model = model,
            SystemFingerprint = systemFingerprint,
            Choices =
            [
                new OpenAIChoiceDto()
                {
                    Index = 0,
                    Delta = new()
                    {
                        Content = content,
                        Role = "assistant"
                    },
                    FinishReason = null
                }
            ]
        };

        await context.Response.WriteAsync("data: " + JsonSerializer.Serialize(openAiResult, new JsonSerializerOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        }) + "\n\n", Encoding.UTF8);
        await context.Response.Body.FlushAsync();
    }

    private static async ValueTask WriteOpenAiResultAsync(HttpContext context, string content)
    {
        var openAiResult = new OpenAIResultDto()
        {
            Id = Guid.NewGuid().ToString("N"),
            _object = "chat.completion.chunk",
            Created = DateTimeOffset.Now.ToUnixTimeSeconds(),
            SystemFingerprint = Guid.NewGuid().ToString("N"),
            Choices =
            [
                new OpenAIChoiceDto()
                {
                    Index = 0,
                    Delta = new()
                    {
                        Content = content,
                        Role = "assistant"
                    },
                    FinishReason = null
                }
            ]
        };

        await context.Response.WriteAsync("data: " + JsonSerializer.Serialize(openAiResult, new JsonSerializerOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        }) + "\n\n", Encoding.UTF8);
        await context.Response.Body.FlushAsync();
    }

    /// <summary>
    /// 权重算法
    /// </summary>
    /// <param Name="channel"></param>
    /// <returns></returns>
    private static ChatChannel CalculateWeight(IEnumerable<ChatChannel> channel)
    {
        // order越大，权重越大，order越小，权重越小，然后随机一个
        var chatChannels = channel as ChatChannel[] ?? channel.ToArray();
        var total = chatChannels.Sum(x => x.Order);

        var random = new Random();

        var value = random.Next(0, total);

        var result = chatChannels.First(x =>
        {
            value -= x.Order;
            return value <= 0;
        });

        return result;
    }

    private static async Task WriteEndAsync(HttpContext context)
    {
        await context.Response.WriteAsync("data: [DONE]\n\n");
        await context.Response.Body.FlushAsync();
    }

    public static async ValueTask WriteEndAsync(HttpContext context, string content)
    {
        await WriteOpenAiResultAsync(context, content);
        await WriteEndAsync(context);
    }

    private static decimal GetCompletionRatio(string name)
    {
        if (CompletionRate.TryGetValue(name, out var ratio))
        {
            return ratio;
        }

        if (name.StartsWith("gpt-3.5"))
        {
            if (name.EndsWith("0125"))
            {
                // https://openai.com/blog/new-embedding-models-and-api-updates
                // Updated GPT-3.5 Turbo model and lower pricing
                return 3;
            }

            if (name.EndsWith("1106"))
            {
                return 2;
            }

            if (name is "gpt-3.5-turbo" or "gpt-3.5-turbo-16k")
            {
                // TODO: clear this after 2023-12-11
                DateTime now = DateTime.UtcNow;
                DateTime cutOffDate = new DateTime(2023, 12, 11, 0, 0, 0, DateTimeKind.Utc);

                // 如果当前日期在2023年12月11日之后，返回2
                if (now > cutOffDate)
                {
                    return 2;
                }
            }

            return 1.333333m;
        }

        if (name.StartsWith("gpt-4"))
        {
            if (name.EndsWith("preview"))
            {
                return 3;
            }

            return 2;
        }

        if (name.StartsWith("claude-instant-1"))
        {
            return 3.38m;
        }

        if (name.StartsWith("claude-2"))
        {
            return 2.965517m;
        }

        if (name.StartsWith("mistral-"))
        {
            return 3;
        }

        return 1;
    }
}