﻿using AIDotNet.Abstractions;
using AIDotNet.Abstractions.Dto;
using AIDotNet.Abstractions.Exceptions;
using AIDotNet.Abstractions.ObjectModels.ObjectModels.RequestModels;
using AIDotNet.API.Service.Domain;
using AIDotNet.API.Service.Exceptions;
using AIDotNet.API.Service.Infrastructure;
using AIDotNet.API.Service.Infrastructure.Helper;
using MapsterMapper;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using System.Text.Json;
using AIDotNet.Abstractions.ObjectModels.ObjectModels.ResponseModels;
using AIDotNet.API.Service.Options;

namespace AIDotNet.API.Service.Service;

public sealed class ChatService(
    IServiceProvider serviceProvider,
    ChannelService channelService,
    TokenService tokenService,
    ImageService imageService,
    RateLimitModelService rateLimitModelService,
    IServiceCache serviceCache,
    UserService userService,
    IMapper mapper,
    LoggerService loggerService)
    : ApplicationService(serviceProvider)
{
    private const string ConsumerTemplate = "模型倍率：{0} 补全倍率：{1}";


    private static readonly Dictionary<string, Dictionary<string, double>> ImageSizeRatios = new()
    {
        {
            "dall-e-2", new Dictionary<string, double>
            {
                { "256x256", 1 },
                { "512x512", 1.125 },
                { "1024x1024", 1.25 }
            }
        },
        {
            "dall-e-3", new Dictionary<string, double>
            {
                { "1024x1024", 1 },
                { "1024x1792", 2 },
                { "1792x1024", 2 }
            }
        },
        {
            "ali-stable-diffusion-xl", new Dictionary<string, double>
            {
                { "512x1024", 1 },
                { "1024x768", 1 },
                { "1024x1024", 1 },
                { "576x1024", 1 },
                { "1024x576", 1 }
            }
        },
        {
            "ali-stable-diffusion-v1.5", new Dictionary<string, double>
            {
                { "512x1024", 1 },
                { "1024x768", 1 },
                { "1024x1024", 1 },
                { "576x1024", 1 },
                { "1024x576", 1 }
            }
        },
        {
            "wanx-v1", new Dictionary<string, double>
            {
                { "1024x1024", 1 },
                { "720x1280", 1 },
                { "1280x720", 1 }
            }
        }
    };

    public async ValueTask ImageAsync(HttpContext context)
    {
        try
        {
            var (token, user) = await tokenService.CheckTokenAsync(context);

            using var body = new MemoryStream();
            await context.Request.Body.CopyToAsync(body);

            var module = JsonSerializer.Deserialize<ImageCreateRequest>(body.ToArray());

            if (module?.Model.IsNullOrEmpty() == true)
            {
                module.Model = "dall-e-2";
            }

            await rateLimitModelService.CheckAsync(module.Model, context, serviceCache);

            var imageCostRatio = GetImageCostRatio(module);

            var rate = SettingService.PromptRate[module.Model];

            var quota = (int)(rate * imageCostRatio * 1000) * module.N;

            if (module == null)
            {
                throw new Exception("模型校验异常");
            }

            if (quota > user.ResidualCredit)
            {
                throw new InsufficientQuotaException("账号余额不足请充值");
            }

            // 获取渠道 通过算法计算权重
            var channel = CalculateWeight((await channelService.GetChannelsAsync())
                .Where(x => x.Models.Contains(module.Model)));

            if (channel == null)
            {
                throw new NotModelException(module.Model);
            }

            // 获取渠道指定的实现类型的服务
            var openService = GetKeyedService<IApiImageService>(channel.Type);

            if (openService == null)
            {
                throw new Exception($"并未实现：{channel.Type} 的服务");
            }

            var response = await openService.CreateImage(module, new ChatOptions()
            {
                Key = channel.Key,
                Address = channel.Address,
                Other = channel.Other
            }, context.RequestAborted);

            await context.Response.WriteAsJsonAsync(new AIDotNetImageCreateResponse()
            {
                data = response.Results,
                created = response.CreatedAt,
                successful = response.Successful
            });


            await loggerService.CreateConsumeAsync(string.Format(ConsumerTemplate, rate, 0), module.Model,
                0, 0, quota ?? 0, token?.Name, user?.UserName, user?.Id, channel.Id,
                channel.Name);

            await userService.ConsumeAsync(user!.Id, quota ?? 0, 0, token?.Key, channel.Id);
        }
        catch (RateLimitException)
        {
            context.Response.StatusCode = 429;
        }
        catch (UnauthorizedAccessException e)
        {
            context.Response.StatusCode = 401;
        }
        catch (Exception e)
        {
            GetLogger<ChatService>().LogError(e.Message);
            await context.WriteErrorAsync(e.Message);
        }
    }

    public async ValueTask EmbeddingAsync(HttpContext context)
    {
        try
        {
            using var body = new MemoryStream();
            await context.Request.Body.CopyToAsync(body);

            var module = JsonSerializer.Deserialize<EmbeddingInput>(body.ToArray());

            if (module == null)
            {
                throw new Exception("模型校验异常");
            }

            await rateLimitModelService.CheckAsync(module!.Model, context, serviceCache);

            var (token, user) = await tokenService.CheckTokenAsync(context);

            // 获取渠道 通过算法计算权重
            var channel = CalculateWeight((await channelService.GetChannelsAsync())
                .Where(x => x.Models.Contains(module.Model)));

            if (channel == null)
            {
                throw new NotModelException(module.Model);
            }

            // 获取渠道指定的实现类型的服务
            var openService = GetKeyedService<IApiTextEmbeddingGeneration>(channel.Type);

            if (openService == null)
            {
                throw new Exception($"并未实现：{channel.Type} 的服务");
            }

            var embeddingCreateRequest = new EmbeddingCreateRequest()
            {
                Model = module.Model,
                EncodingFormat = module.EncodingFormat,
            };

            int requestToken;
            if (module.Input is JsonElement str)
            {
                if (str.ValueKind == JsonValueKind.String)
                {
                    embeddingCreateRequest.Input = str.ToString();
                    requestToken = TokenHelper.GetTotalTokens(str.ToString());
                }
                else if (str.ValueKind == JsonValueKind.Array)
                {
                    var inputString = str.EnumerateArray().Select(x => x.ToString()).ToArray();
                    embeddingCreateRequest.InputAsList = inputString.ToList();
                    requestToken = TokenHelper.GetTotalTokens(inputString);
                }
                else
                {
                    throw new Exception("输入格式错误");
                }
            }
            else
            {
                throw new Exception("输入格式错误");
            }

            var stream = await openService.EmbeddingAsync(embeddingCreateRequest, new ChatOptions()
            {
                Key = channel.Key,
                Address = channel.Address,
                Other = channel.Other
            }, context.RequestAborted);

            if (SettingService.PromptRate.TryGetValue(module.Model, out var rate))
            {
                var quota = requestToken * rate;

                var completionRatio = GetCompletionRatio(module.Model);
                quota += (rate * completionRatio);

                // 将quota 四舍五入
                quota = Math.Round(quota, 0, MidpointRounding.AwayFromZero);

                await loggerService.CreateConsumeAsync(string.Format(ConsumerTemplate, rate, completionRatio),
                    module.Model,
                    requestToken, 0, (int)quota, token?.Name, user?.UserName, user?.Id, channel.Id,
                    channel.Name);

                await userService.ConsumeAsync(user!.Id, (long)quota, requestToken, token?.Key, channel.Id);
            }

            await context.Response.WriteAsJsonAsync(stream);
        }
        catch (RateLimitException)
        {
            context.Response.StatusCode = 429;
        }
        catch (UnauthorizedAccessException e)
        {
            context.Response.StatusCode = 401;
        }
        catch (Exception e)
        {
            GetLogger<ChatService>().LogError(e.Message);
            await context.WriteErrorAsync(e.Message);
        }
    }

    public async ValueTask CompletionsAsync(HttpContext context)
    {
        using var body = new MemoryStream();
        await context.Request.Body.CopyToAsync(body);

        var module = JsonSerializer.Deserialize<CompletionCreateRequest>(body.ToArray());

        if (module == null)
        {
            throw new Exception("模型校验异常");
        }

        try
        {
            await rateLimitModelService.CheckAsync(module!.Model, context, serviceCache);

            var (token, user) = await tokenService.CheckTokenAsync(context);

            // 获取渠道 通过算法计算权重
            var channel = CalculateWeight((await channelService.GetChannelsAsync())
                .Where(x => x.Models.Contains(module.Model)));

            if (channel == null)
            {
                throw new NotModelException(module.Model);
            }

            var openService = GetKeyedService<IApiCompletionService>(channel.Type);

            if (openService == null)
            {
                throw new Exception($"并未实现：{channel.Type} 的服务");
            }

            if (SettingService.PromptRate.TryGetValue(module.Model, out var rate))
            {
                if (module.Stream == false)
                {
                    var (requestToken, responseToken) =
                        await CompletionsHandlerAsync(context, module, channel, openService, user, rate);

                    var quota = requestToken * rate;

                    var completionRatio = GetCompletionRatio(module.Model);
                    quota += responseToken * (rate * completionRatio);

                    // 将quota 四舍五入
                    quota = Math.Round(quota, 0, MidpointRounding.AwayFromZero);

                    await loggerService.CreateConsumeAsync(string.Format(ConsumerTemplate, rate, completionRatio),
                        module.Model,
                        requestToken, responseToken, (int)quota, token?.Name, user?.UserName, user?.Id, channel.Id,
                        channel.Name);

                    await userService.ConsumeAsync(user!.Id, (long)quota, requestToken, token?.Key, channel.Id);
                }
            }
            else
            {
                context.Response.StatusCode = 200;
                if (module.Stream == true)
                {
                    await context.WriteStreamErrorAsync("当前模型未设置倍率");
                }
                else
                {
                    await context.WriteErrorAsync("当前模型未设置倍率");
                }
            }
        }
        catch (RateLimitException)
        {
            context.Response.StatusCode = 429;
        }
        catch (Exception e)
        {
            GetLogger<ChatService>().LogError(e.Message);
            await context.WriteErrorAsync(e.Message);
        }
    }

    public async ValueTask<(int, int)> CompletionsHandlerAsync(HttpContext context, CompletionCreateRequest input,
        ChatChannel channel, IApiCompletionService openService, User user, decimal rate)
    {
        var setting = new ChatOptions()
        {
            Key = channel.Key,
            Address = channel.Address,
            Other = channel.Other
        };

        var requestToken = TokenHelper.GetTotalTokens(input.Prompt ?? string.Empty);

        var result = await openService.CompletionAsync(input, setting);

        var responseToken = TokenHelper.GetTotalTokens(result.Choices.FirstOrDefault()?.Text ?? string.Empty);

        return (requestToken, responseToken);
    }

    public async ValueTask ChatCompletionsAsync(HttpContext context)
    {
        using var body = new MemoryStream();
        await context.Request.Body.CopyToAsync(body);

        var module = JsonSerializer.Deserialize<ChatCompletionCreateRequest>(body.ToArray());

        if (module == null)
        {
            throw new Exception("模型校验异常");
        }

        try
        {
            await rateLimitModelService.CheckAsync(module!.Model, context, serviceCache);

            var (token, user) = await tokenService.CheckTokenAsync(context);

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
                throw new Exception($"并未实现：{channel.Type} 的服务");
            }

            if (SettingService.PromptRate.TryGetValue(module.Model, out var rate))
            {
                int requestToken;
                int responseToken = 0;

                if (module.Stream == true)
                {
                    (requestToken, responseToken) =
                        await StreamChatHandlerAsync(context, module, channel, openService, user, rate);
                }
                else
                {
                    (requestToken, responseToken) =
                        await ChatHandlerAsync(context, module, channel, openService, user, rate);
                }

                var quota = requestToken * rate;

                var completionRatio = GetCompletionRatio(module.Model);
                quota += responseToken * (rate * completionRatio);

                // 将quota 四舍五入
                quota = Math.Round(quota, 0, MidpointRounding.AwayFromZero);

                await loggerService.CreateConsumeAsync(string.Format(ConsumerTemplate, rate, completionRatio),
                    module.Model,
                    requestToken, responseToken, (int)quota, token?.Name, user?.UserName, user?.Id, channel.Id,
                    channel.Name);

                await userService.ConsumeAsync(user!.Id, (long)quota, requestToken, token?.Key, channel.Id);
            }
            else
            {
                context.Response.StatusCode = 200;
                if (module.Stream == true)
                {
                    await context.WriteStreamErrorAsync("当前模型未设置倍率");
                }
                else
                {
                    await context.WriteErrorAsync("当前模型未设置倍率");
                }
            }
        }
        catch (RateLimitException)
        {
            context.Response.StatusCode = 429;
        }
        catch (UnauthorizedAccessException e)
        {
            context.Response.StatusCode = 401;
        }
        catch (OpenAIErrorException error)
        {
            context.Response.StatusCode = 400;
            if (module.Stream == true)
            {
                await context.WriteStreamErrorAsync(error.Message, error.Code);
            }
            else
            {
                await context.WriteErrorAsync(error.Message, error.Code);
            }
        }
        catch (Exception e)
        {
            GetLogger<ChatService>().LogError(e.Message);
            if (module.Stream == true)
            {
                await context.WriteStreamErrorAsync(e.Message);
            }
            else
            {
                await context.WriteErrorAsync(e.Message);
            }
        }
    }

    private async ValueTask<(int, int)> ChatHandlerAsync(HttpContext context, ChatCompletionCreateRequest input,
        ChatChannel channel, IApiChatCompletionService openService, User user, decimal rate)
    {
        int requestToken;
        int responseToken;

        var setting = new ChatOptions()
        {
            Key = channel.Key,
            Address = channel.Address,
            Other = channel.Other
        };

        // 这里应该用其他的方式来判断是否是vision模型，目前先这样处理
        if (input.Messages.Any(x => x.Contents != null))
        {
            requestToken = TokenHelper.GetTotalTokens(input?.Messages.SelectMany(x => x.Contents)
                .Where(x => x.Type == "text").Select(x => x.Text).ToArray());

            // 解析图片
            foreach (var message in input.Messages.SelectMany(x => x.Contents)
                         .Where(x => x.Type is "image" or "image_url"))
            {
                var imageUrl = message.ImageUrl;
                if (imageUrl != null)
                {
                    var url = imageUrl.Url;
                    var detail = "";
                    if (!imageUrl.Detail.IsNullOrEmpty())
                    {
                        detail = imageUrl.Detail;
                    }

                    try
                    {
                        var imageTokens = await CountImageTokens(url, detail);
                        requestToken += imageTokens.Item1;
                    }
                    catch (Exception ex)
                    {
                        GetLogger<ChatService>().LogError("Error counting image tokens: " + ex.Message);
                    }
                }
            }

            var quota = requestToken * rate;

            // 判断请求token数量是否超过额度
            if (quota > user.ResidualCredit)
            {
                throw new InsufficientQuotaException("账号余额不足请充值");
            }

            var result = await openService.CompleteChatAsync(input, setting);

            await context.Response.WriteAsJsonAsync(result);

            responseToken = TokenHelper.GetTokens(result.Choices.FirstOrDefault()?.Delta.Content ?? string.Empty);
        }
        else
        {
            requestToken = TokenHelper.GetTotalTokens(input?.Messages.Select(x => x.Content).ToArray());

            var quota = requestToken * rate;

            // 判断请求token数量是否超过额度
            if (quota > user.ResidualCredit)
            {
                throw new InsufficientQuotaException("账号余额不足请充值");
            }

            var result = await openService.CompleteChatAsync(input, setting);

            await context.Response.WriteAsJsonAsync(result);

            responseToken = TokenHelper.GetTokens(result.Choices.FirstOrDefault()?.Delta.Content ?? string.Empty);
        }

        return (requestToken, responseToken);
    }

    /// <summary>
    /// Stream 对话处理
    /// </summary>
    /// <param name="context"></param>
    /// <param name="input">输入</param>
    /// <param name="channel">渠道</param>
    /// <param name="openService"></param>
    /// <param name="user"></param>
    /// <param Name="context"></param>
    /// <param Name="body"></param>
    /// <param Name="module"></param>
    /// <param Name="channel"></param>
    /// <param Name="openService"></param>
    /// <param name="rate"></param>
    /// <returns></returns>
    private async ValueTask<(int, int)> StreamChatHandlerAsync(HttpContext context,
        ChatCompletionCreateRequest input, ChatChannel channel, IApiChatCompletionService openService, User user,
        decimal rate)
    {
        int requestToken;

        var setting = new ChatOptions()
        {
            Key = channel.Key,
            Address = channel.Address,
            Other = channel.Other
        };

        var responseMessage = new StringBuilder();

        context.Response.Headers.ContentType = "text/event-stream";

        if (input.Messages.Any(x => x.Contents != null))
        {
            requestToken = TokenHelper.GetTotalTokens(input?.Messages.Where(x => x is { Contents: not null })
                .SelectMany(x => x!.Contents)
                .Where(x => x.Type == "text")
                .Select(x => x.Text).ToArray());

            // 解析图片
            foreach (var message in input.Messages.Where(x => x is { Contents: not null }).SelectMany(x => x.Contents)
                         .Where(x => x.Type is "image" or "image_url"))
            {
                var imageUrl = message.ImageUrl;
                if (imageUrl != null)
                {
                    var url = imageUrl.Url;
                    var detail = "";
                    if (!imageUrl.Detail.IsNullOrEmpty())
                    {
                        detail = imageUrl.Detail;
                    }

                    try
                    {
                        var imageTokens = await CountImageTokens(url, detail);
                        requestToken += imageTokens.Item1;
                    }
                    catch (Exception ex)
                    {
                        GetLogger<ChatService>().LogError("Error counting image tokens: " + ex.Message);
                    }
                }
            }

            var quota = requestToken * rate;
            // 判断请求token数量是否超过额度
            if (quota > user.ResidualCredit)
            {
                throw new InsufficientQuotaException("账号余额不足请充值");
            }

            await foreach (var item in openService.StreamChatAsync(input, setting))
            {
                foreach (var response in item.Choices)
                {
                    if (response.Delta.Role.IsNullOrEmpty())
                    {
                        response.Delta.Role = "assistant";
                    }

                    if (response.Message.Role.IsNullOrEmpty())
                    {
                        response.Message.Role = "assistant";
                    }

                    if (string.IsNullOrEmpty(response.Delta.Content))
                    {
                        response.Delta.Content = null;
                        response.Message.Content = null;
                    }
                }
                responseMessage.Append(item.Choices?.FirstOrDefault()?.Delta.Content ?? string.Empty);
                await context.WriteResultAsync(item).ConfigureAwait(false);
            }

            await context.WriteEndAsync();
        }
        else
        {
            requestToken = TokenHelper.GetTotalTokens(input?.Messages.Select(x => x.Content).ToArray());


            var quota = requestToken * rate;

            // 判断请求token数量是否超过额度
            if (quota > user.ResidualCredit)
            {
                throw new InsufficientQuotaException("账号余额不足请充值");
            }

            await foreach (var item in openService.StreamChatAsync(input, setting))
            {
                if (item.Error != null && !string.IsNullOrEmpty(item.Error.Message))
                {
                    throw new OpenAIErrorException(item.Error.Message, item.Error.Code ?? "error");
                }

                foreach (var response in item.Choices)
                {
                    if (response.Delta.Role.IsNullOrEmpty())
                    {
                        response.Delta.Role = "assistant";
                    }

                    if (response.Message.Role.IsNullOrEmpty())
                    {
                        response.Message.Role = "assistant";
                    }

                    if (string.IsNullOrEmpty(response.Delta.Content))
                    {
                        response.Delta.Content = null;
                        response.Message.Content = null;
                    }
                }

                responseMessage.Append(item.Choices?.FirstOrDefault()?.Delta.Content ?? string.Empty);
                await context.WriteResultAsync(item).ConfigureAwait(false);
            }

            await context.WriteEndAsync();
        }

        var responseToken = TokenHelper.GetTokens(responseMessage.ToString());

        return (requestToken, responseToken);
    }

    public bool IsVision(string model)
    {
        if (ChatCoreOptions.VisionModel.Any(model.Contains))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// 权重算法
    /// </summary>
    /// <param Name="channel"></param>
    /// <returns></returns>
    private static ChatChannel CalculateWeight(IEnumerable<ChatChannel> channel)
    {
        var chatChannels = channel as ChatChannel[] ?? channel.ToArray();
        var total = chatChannels.Sum(x => x.Order);

        if (chatChannels.Length == 0)
        {
            throw new NotModelException($"没有可用的渠道。");
        }

        var value = Random.Shared.NextDouble() * total;

        foreach (var chatChannel in chatChannels)
        {
            value -= chatChannel.Order;
            if (value <= 0)
            {
                return chatChannel;
            }
        }

        return chatChannels.Last();
    }

    /// <summary>
    /// 对话模型补全倍率
    /// </summary>
    /// <param name="name"></param>
    /// <returns></returns>
    private static decimal GetCompletionRatio(string name)
    {
        if (SettingService.CompletionRate.TryGetValue(name, out var ratio))
        {
            return ratio;
        }

        if (name.StartsWith("gpt-3.5"))
        {
            if (name == "gpt-3.5-turbo" || name.EndsWith("0125"))
            {
                return 3;
            }

            if (name.EndsWith("1106"))
            {
                return 2;
            }

            return (decimal)(4.0 / 3.0);
        }

        if (name.StartsWith("gpt-4"))
        {
            return name.StartsWith("gpt-4-turbo") ? 3 : 2;
        }

        if (name.StartsWith("claude-"))
        {
            return name.StartsWith("claude-3") ? 5 : 3;
        }

        if (name.StartsWith("mistral-") || name.StartsWith("gemini-"))
        {
            return 3;
        }

        return name switch
        {
            "llama2-70b-4096" => new decimal(0.8 / 0.7),
            _ => 1
        };
    }

    /// <summary>
    /// 计算图片倍率
    /// </summary>
    /// <param name="module"></param>
    /// <returns></returns>
    private static decimal GetImageCostRatio(ImageCreateRequest module)
    {
        var imageCostRatio = GetImageSizeRatio(module.Model, module.Size);
        if (module is { Quality: "hd", Model: "dall-e-3" })
        {
            if (module.Size == "1024x1024")
            {
                imageCostRatio *= 2;
            }
            else
            {
                imageCostRatio *= (decimal)1.5;
            }
        }

        return imageCostRatio;
    }

    /// <summary>
    /// 计算图片倍率
    /// </summary>
    /// <param name="model"></param>
    /// <param name="size"></param>
    /// <returns></returns>
    public static decimal GetImageSizeRatio(string model, string size)
    {
        if (!ImageSizeRatios.TryGetValue(model, out var ratios)) return 1;

        if (ratios.TryGetValue(size, out var ratio))
        {
            return (decimal)ratio;
        }

        return 1;
    }

    /// <summary>
    /// 计算图片token
    /// </summary>
    /// <param name="url"></param>
    /// <param name="detail"></param>
    /// <returns></returns>
    public async ValueTask<Tuple<int, Exception>> CountImageTokens(string url, string detail)
    {
        bool fetchSize = true;
        int width = 0, height = 0;
        int lowDetailCost = 20; // Assuming lowDetailCost is 20
        int highDetailCostPerTile = 100; // Assuming highDetailCostPerTile is 100
        int additionalCost = 50; // Assuming additionalCost is 50

        if (string.IsNullOrEmpty(detail) || detail == "auto")
        {
            detail = "high";
        }

        switch (detail)
        {
            case "low":
                return new Tuple<int, Exception>(lowDetailCost, null);
            case "high":
                if (fetchSize)
                {
                    try
                    {
                        (width, height) = await imageService.GetImageSize(url);
                    }
                    catch (Exception e)
                    {
                        return new Tuple<int, Exception>(0, e);
                    }
                }

                if (width > 2048 || height > 2048)
                {
                    double ratio = 2048.0 / Math.Max(width, height);
                    width = (int)(width * ratio);
                    height = (int)(height * ratio);
                }

                if (width > 768 && height > 768)
                {
                    double ratio = 768.0 / Math.Min(width, height);
                    width = (int)(width * ratio);
                    height = (int)(height * ratio);
                }

                int numSquares = (int)Math.Ceiling((double)width / 512) * (int)Math.Ceiling((double)height / 512);
                int result = numSquares * highDetailCostPerTile + additionalCost;
                return new Tuple<int, Exception>(result, null);
            default:
                return new Tuple<int, Exception>(0, new Exception("Invalid detail option"));
        }
    }
}