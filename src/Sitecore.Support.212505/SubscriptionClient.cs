namespace Sitecore.Support.EDS.Providers.SparkPost.Subscription
{
  using System;
  using System.Collections.Generic;
  using System.Linq;
  using System.Net;
  using System.Net.Http;
  using System.Threading.Tasks;
  using Sitecore.Diagnostics;
  using Sitecore.EDS.Core.Net.Http;
  using Sitecore.EDS.Providers.SparkPost.Configuration;
  using Sitecore.EDS.Providers.SparkPost.Exceptions;
  using Sitecore.EDS.Providers.SparkPost.Subscription.Models;
  using Sitecore.ExM.Framework.Diagnostics;
  using Sitecore.ExM.Framework.Formatters;
  using Sitecore.ExM.Framework.Helpers;
  using Sitecore.EDS.Providers.SparkPost.Subscription;

  public class SubscriptionClient : ISubscriptionClient
  {
    [NotNull]
    private readonly IConfigurationStore _configurationStore;

    [NotNull]
    private readonly string _credentialsType;

    [NotNull]
    private readonly IHttpClientFactory _httpFactory;

    [NotNull]
    private readonly ILogger _logger;

    [NotNull]
    private readonly IRetry _retry;

    [NotNull]
    private readonly IList<int> _retryableHttpErrorCodes = new List<int>();

    [NotNull]
    private readonly string _sparkpostApplicationId;

    [NotNull]
    private readonly IDateTimeFormatter _dateTimeFormatter;

    /// <summary>
    /// Initializes a new instance of the <see cref="SubscriptionClient" /> class.
    /// </summary>
    /// <param name="httpFactory">The HTTP factory.</param>
    /// <param name="configurationStore">The configuration store.</param>
    /// <param name="logger">The logger</param>
    /// <param name="retry">The retry operation helper</param>
    /// <param name="credentialsType">The credentials type</param>
    /// <param name="sparkpostApplicationId">The SparkPost application id</param>
    /// <param name="dateTimeFormatter">The <see cref="IDateTimeFormatter"/></param>
    public SubscriptionClient([NotNull] IHttpClientFactory httpFactory, [NotNull] IConfigurationStore configurationStore, [NotNull] ILogger logger,
        [NotNull] IRetry retry, [NotNull] string credentialsType, [NotNull] string sparkpostApplicationId, [NotNull] IDateTimeFormatter dateTimeFormatter)
    {
      Assert.ArgumentNotNull(httpFactory, "httpFactory");
      Assert.ArgumentNotNull(configurationStore, "configurationStore");
      Assert.ArgumentNotNull(logger, "logger");
      Assert.ArgumentNotNull(retry, "retry");
      Assert.ArgumentNotNull(credentialsType, "credentialsType");
      Assert.ArgumentNotNull(sparkpostApplicationId, "sparkpostApplicationId");

      _httpFactory = httpFactory;
      _configurationStore = configurationStore;
      _logger = logger;
      _retry = retry;
      _credentialsType = credentialsType;
      _sparkpostApplicationId = sparkpostApplicationId;
      _dateTimeFormatter = dateTimeFormatter;
    }

    /// <summary>
    /// Gets the subscription information for a specific license
    /// </summary>
    /// <param name="licenseId">The license id</param>
    /// <returns>The subscription information, or null if the subscription could not be determined</returns>
    public async Task<Sitecore.EDS.Providers.SparkPost.Subscription.Models.Subscription> GetSubscription([NotNull] string licenseId)
    {
      Assert.ArgumentNotNull(licenseId, "licenseId");

      var restRequest = CreateGetRequest(string.Format(SubscriptionApi.Urls.Subscription, licenseId));

      var response = await _retry.OperationWithBasicRetryAsync(() => ExecuteHttpRequestBaseAsync<Sitecore.EDS.Providers.SparkPost.Subscription.Models.Subscription>(restRequest), IsTransient);

      if (response?.Data?.Value == null)
      {
        _logger.LogInfo($"Subscription not found for license id {licenseId}.");
        return null;
      }

      var subscriptions = response.Data.Value;
      if (subscriptions.Length == 1)
      {
        return subscriptions.Single();
      }

      _logger.LogInfo($"{subscriptions.Length} subscriptions found for license id {licenseId}. Expected only one.");
      return null;
    }

    /// <summary>
    /// Gets the order information for a specific subscription and the configured SparkPost application id
    /// </summary>
    /// <param name="subscriptionId">The subscription id</param>
    /// <returns>The order information, or null if the order could not be determiend</returns>
    public async Task<Order> GetOrder(string subscriptionId)
    {
      Assert.ArgumentNotNull(subscriptionId, "subscriptionId");

      var restRequest = CreateGetRequest(string.Format(SubscriptionApi.Urls.Order, subscriptionId, _sparkpostApplicationId));

      var response = await _retry.OperationWithBasicRetryAsync(() => ExecuteHttpRequestBaseAsync<Order>(restRequest), IsTransient);

      if (response?.Data?.Value == null)
      {
        _logger.LogInfo($"Order not found for subscription id {subscriptionId}.");
        return null;
      }

      var orders = response.Data.Value;
      if (orders.Length == 1)
      {
        return orders.Single();
      }

      _logger.LogInfo($"{orders.Length} orders found for subscription id {subscriptionId}. Expected only one.");
      return null;
    }

    /// <summary>
    /// Gets the usage information for the specified subscription, the configured SparkPost application id, and the specified period
    /// </summary>
    /// <param name="subscriptionId">The subscription id</param>
    /// <param name="startDate">The start date</param>
    /// <param name="endDate">The end date</param>
    /// <returns>The usage for the specified period</returns>
    public async Task<Usage> GetUsage(string subscriptionId, DateTime startDate, DateTime endDate)
    {
      Assert.ArgumentNotNull(subscriptionId, "subscriptionId");

      //var restRequest = CreateGetRequest(string.Format(SubscriptionApi.Urls.Consumption, subscriptionId, _sparkpostApplicationId, _dateTimeFormatter.FormatShortDate(startDate), _dateTimeFormatter.FormatShortDate(endDate)));

      // Resolves issue #212505
      var restRequest = CreateGetRequest(string.Format(SubscriptionApi.Urls.Consumption, subscriptionId, _sparkpostApplicationId, startDate.ToShortDateString(), endDate.ToShortDateString()));

      var response = await _retry.OperationWithBasicRetryAsync(() => ExecuteHttpRequestBaseAsync<Usage>(restRequest), IsTransient);

      if (response?.Data?.Value == null)
      {
        _logger.LogInfo($"Usage not found for subscription id {subscriptionId}.");
        return null;
      }

      var usage = response.Data.Value;
      if (usage.Length == 1)
      {
        return usage.Single();
      }

      _logger.LogInfo($"{usage.Length} usages found for subscription id {subscriptionId}. Expected only one.");
      return null;
    }

    /// <summary>
    /// Executes the HTTP request.
    /// </summary>
    /// <typeparam name="TResult">The type of the result.</typeparam>
    /// <param name="httpRequest">The HTTP request.</param>
    /// <param name="expectedHttpStatusCodes">The http status codes we expect the request to return</param>
    /// <returns>
    /// The request result data <see cref="Task{TResult}"/>.
    /// </returns>
    private async Task<HttpResponse<Response<TResult>>> ExecuteHttpRequestBaseAsync<TResult>(IHttpRequest httpRequest, List<int> expectedHttpStatusCodes = null)
    {
      if (expectedHttpStatusCodes == null)
      {
        expectedHttpStatusCodes = new List<int>
                {
                    (int) HttpStatusCode.OK
                };
      }

      using (var httpClient = _httpFactory.Create(_configurationStore.SubscriptionApiUrl))
      {
        var response = await httpClient.ExecuteAsync<Response<TResult>>(httpRequest);

        if (expectedHttpStatusCodes.Contains((int)response.StatusCode))
        {
          return response;
        }

        if (response.HasErrors)
        {
          throw new SparkPostServiceException(response.StatusCode, string.Format("Error calling service ended up with status: {{{0}}} and content {{{1}}} ", response.StatusCode, response.ReasonPhrase));
        }

        return response;
      }
    }

    private bool IsTransient(Exception ex)
    {
      var exception = ex as SparkPostServiceException;

      if (exception == null)
      {
        _logger.LogError("Request to app center failed", ex);
      }

      return exception != null && _retryableHttpErrorCodes.Contains((int)exception.HttpStatusCode);
    }

    /// <summary>
    /// Registers a http status code <paramref name="retryableHttpErrorCode"/> that, if returned, causes the service
    /// to retry the request.
    /// </summary>
    /// <param name="retryableHttpErrorCode">The http status code that should allow the service to retry the request</param>
    public void AddRetryableHttpStatusCode([NotNull] string retryableHttpErrorCode)
    {
      Assert.ArgumentNotNull(retryableHttpErrorCode, "retryableHttpErrorCode");
      var httpStatusCode = AssertString.ArgumentToInt(retryableHttpErrorCode, "retryableHttpErrorCode", "The value must be an integer");

      _retryableHttpErrorCodes.Add(httpStatusCode);
    }

    /// <summary>
    /// Creates a HTTP request using the GET verb.
    /// </summary>
    /// <param name="url">The URL.</param>
    /// <returns>
    /// New request object <see cref="HttpRequest"/>
    /// </returns>
    private HttpRequest CreateGetRequest(string url)
    {
      return CreateRequest(url, HttpMethod.Get);
    }

    /// <summary>
    /// Creates a HTTP request
    /// </summary>
    /// <param name="url">The URL.</param>
    /// <param name="method">The http method verb.</param>
    /// <returns>
    /// New request object <see cref="HttpRequest"/>
    /// </returns>
    private HttpRequest CreateRequest(string url, HttpMethod method)
    {
      var request = new HttpRequest
      {
        Url = url,
        Method = method
      };

      request.AddRequestParameter(SubscriptionApi.Parameters.NexusKey, _configurationStore.GetCloudAuthenticationKey(), ParameterType.Header);
      request.AddRequestParameter(SubscriptionApi.Parameters.CredentialsType, _credentialsType, ParameterType.Header);

      return request;
    }
  }
}