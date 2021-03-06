using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;

namespace EasyNetQ
{
    public class RabbitBus : IBus
    {
        private readonly SerializeType serializeType;
        private readonly ISerializer serializer;
        private readonly IPersistentConnection connection;
        private readonly IConsumerFactory consumerFactory;
        private readonly IEasyNetQLogger logger;
		private readonly Func<string> getCorrelationId;
		private readonly IConventions conventions;

        private readonly ConcurrentDictionary<int, string> responseQueueNameCache = new ConcurrentDictionary<int, string>();
        private readonly ISet<string> publishExchanges = new ConcurrentHashSet<string>();
        private readonly ISet<string> requestExchanges = new ConcurrentHashSet<string>();
        private readonly List<IModel> modelList = new List<IModel>();

		private readonly ConcurrentBag<Action> subscribeActions;

        public const string RpcExchange = "easy_net_q_rpc";
        public const bool NoAck = false;

        public SerializeType SerializeType
        {
            get { return serializeType; }
        }

        public ISerializer Serializer
        {
            get { return serializer; }
        }

        public IPersistentConnection Connection
        {
            get { return connection; }
        }

        public IConsumerFactory ConsumerFactory
        {
            get { return consumerFactory; }
        }

        public IEasyNetQLogger Logger
        {
            get { return logger; }
        }

        public Func<string> GetCorrelationId
        {
            get { return getCorrelationId; }
        }

        public IConventions Conventions
        {
            get { return conventions; }
        }

        public ConcurrentDictionary<int, string> ResponseQueueNameCache
        {
            get { return responseQueueNameCache; }
        }

        public ISet<string> PublishExchanges
        {
            get { return publishExchanges; }
        }

        public ISet<string> RequestExchanges 
        { 
            get{ return requestExchanges; } 
        }

        public List<IModel> ModelList
        {
            get { return modelList; }
        }

        public ConcurrentBag<Action> SubscribeActions
        {
            get { return subscribeActions; }
        }

        public int OpenChannelCount { get { return modelList.Count; } }


        // prefetchCount determines how many messages will be allowed in the local in-memory queue
        // setting to zero makes this infinite, but risks an out-of-memory exception.
        // set to 50 based on this blog post:
        // http://www.rabbitmq.com/blog/2012/04/25/rabbitmq-performance-measurements-part-2/
        private const int prefetchCount = 50; 

        public RabbitBus(
            SerializeType serializeType, 
            ISerializer serializer, 
            IConsumerFactory consumerFactory, 
            IConnectionFactory connectionFactory, 
            IEasyNetQLogger logger,
			Func<string> getCorrelationId,
			IConventions conventions)
        {
            if(serializeType == null)
            {
                throw new ArgumentNullException("serializeType");
            }
            if(serializer == null)
            {
                throw new ArgumentNullException("serializer");
            }
            if(consumerFactory == null)
            {
                throw new ArgumentNullException("consumerFactory");
            }
            if(connectionFactory == null)
            {
                throw new ArgumentNullException("connectionFactory");
            }
            if(logger == null)
            {
                throw new ArgumentNullException("logger");
            }
            if(getCorrelationId == null)
            {
                throw new ArgumentNullException("getCorrelationId");
            }
            if(conventions == null)
            {
                throw new ArgumentNullException("conventions");
            }

            this.serializeType = serializeType;
            this.consumerFactory = consumerFactory;
            this.logger = logger;
            this.serializer = serializer;
			this.getCorrelationId = getCorrelationId;
			this.conventions = conventions;

            connection = new PersistentConnection(connectionFactory, logger);
            connection.Connected += OnConnected;
            connection.Disconnected += consumerFactory.ClearConsumers;
            connection.Disconnected += OnDisconnected;

			subscribeActions = new ConcurrentBag<Action>();
        }

        public IPublishChannel OpenPublishChannel()
        {
            return new RabbitPublishChannel(this);
        }

        // channels should not be shared between threads.
        private ThreadLocal<IModel> threadLocalPublishChannel = new ThreadLocal<IModel>();

        private void DeclarePublishExchange(IModel channel, string exchangeName)
        {
            // no need to declare on every publish
            if (publishExchanges.Add(exchangeName))
            {
                channel.ExchangeDeclare(
                    exchangeName,               // exchange
                    ExchangeType.Topic,    // type
                    true);                  // durable
            }
        }

        public void CheckMessageType<TMessage>(IBasicProperties properties)
        {
            var typeName = serializeType(typeof (TMessage));
            if (properties.Type != typeName)
            {
                logger.ErrorWrite("Message type is incorrect. Expected '{0}', but was '{1}'",
                    typeName, properties.Type);

                throw new EasyNetQInvalidMessageTypeException("Message type is incorrect. Expected '{0}', but was '{1}'",
                    typeName, properties.Type);
            }
        }

        public void Subscribe<T>(string subscriptionId, Action<T> onMessage)
        {
            Subscribe(subscriptionId, "#", onMessage);
        }

        public void Subscribe<T>(string subscriptionId, string topic, Action<T> onMessage)
        {
            Subscribe(subscriptionId, Enumerable.Repeat(topic, 1), onMessage);
        }

        public void Subscribe<T>(string subscriptionId, IEnumerable<string> topics, Action<T> onMessage)
        {
            SubscribeAsync<T>(subscriptionId, topics, msg =>
            {
                var tcs = new TaskCompletionSource<object>();
                try
                {
                    onMessage(msg);
                    tcs.SetResult(null);
                }
                catch (Exception exception)
                {
                    tcs.SetException(exception);
                }
                return tcs.Task;
            });
        }

        public void SubscribeAsync<T>(string subscriptionId, Func<T, Task> onMessage)
        {
            SubscribeAsync(subscriptionId, "#", onMessage);
        }

        public void SubscribeAsync<T>(string subscriptionId, string topic, Func<T, Task> onMessage)
        {
            SubscribeAsync(subscriptionId, Enumerable.Repeat(topic, 1), onMessage);
        }

        public void SubscribeAsync<T>(string subscriptionId, IEnumerable<string> topics, Func<T, Task> onMessage)
        {
            if (onMessage == null)
            {
                throw new ArgumentNullException("onMessage");
            }

            var queueName = GetQueueName<T>(subscriptionId);
            var exchangeName = GetExchangeName<T>();

            Action subscribeAction = () =>
            {
                var channel = connection.CreateModel();
                modelList.Add(channel);
                DeclarePublishExchange(channel, exchangeName);

                channel.BasicQos(0, prefetchCount, false);

                var queue = channel.QueueDeclare(
                    queueName,          // queue
                    true,               // durable
                    false,              // exclusive
                    false,              // autoDelete
                    null);              // arguments

                foreach(var topic in topics)
                {
                    channel.QueueBind(
                        queue,          // queue
                        exchangeName,   // exchange
                        topic);         // routingKey
                    
                }

                var consumer = consumerFactory.CreateConsumer(channel,
                    (consumerTag, deliveryTag, redelivered, exchange, routingKey, properties, body) =>
                    {
                        CheckMessageType<T>(properties);

                        var message = serializer.BytesToMessage<T>(body);
                        return onMessage(message);
                    });

                channel.BasicConsume(
                    queueName,              // queue
                    NoAck,                  // noAck 
                    consumer.ConsumerTag,   // consumerTag
                    consumer);              // consumer
            };

            AddSubscriptionAction(subscribeAction);
        }

		private string GetExchangeName<T>()
		{
			return conventions.ExchangeNamingConvention(typeof(T));
		}

		private string GetQueueName<T>(string subscriptionId)
		{
			return conventions.QueueNamingConvention(typeof (T), subscriptionId);
		}

        public void Request<TRequest, TResponse>(TRequest request, Action<TResponse> onResponse)
        {
            if (onResponse == null)
            {
                throw new ArgumentNullException("onResponse");
            }
            if (request == null)
            {
                throw new ArgumentNullException("request");
            }
            if (!connection.IsConnected)
            {
                throw new EasyNetQException("Publish failed. No rabbit server connected.");
            }

            // rather than setting up a subscription on each call of Request, we cache a single
            // subscription keyed on the hashcode of the onResponse action. This has a couple of
            // consequences:
            //  1.  Closures don't work as expected since the closed over variable is always the first
            //      one that was called.
            //  2.  Worries about the uniqueness of MethodInfo.GetHashCode. Looking at the CLR source
            //      it seems that it's not overriden so it is the same as Object.GetHashCode(). This
            //      is unique for an instance in an app-domain, so it _should_ be OK for this usage.
            var uniqueResponseQueueName = "EasyNetQ_return_" + Guid.NewGuid().ToString();
            if (responseQueueNameCache.TryAdd(onResponse.Method.GetHashCode(), uniqueResponseQueueName))
            {
                logger.DebugWrite("Setting up return subscription for req/resp {0} {1}", 
                    typeof(TRequest).Name,
                    typeof(TResponse).Name);

                SubscribeToResponse(onResponse, uniqueResponseQueueName);
            }

            var returnQueueName = responseQueueNameCache[onResponse.Method.GetHashCode()];

            RequestPublish(request, returnQueueName);
        }

        private void SubscribeToResponse<TResponse>(Action<TResponse> onResponse, string returnQueueName)
        {
            var responseChannel = connection.CreateModel();
            modelList.Add( responseChannel );

            // respond queue is transient, only exists for the lifetime of the service.
            var respondQueue = responseChannel.QueueDeclare(
                returnQueueName,
                false,              // durable
                true,               // exclusive
                true,               // autoDelete
                null                // arguments
                );

            var consumer = consumerFactory.CreateConsumer(responseChannel,
                (consumerTag, deliveryTag, redelivered, exchange, routingKey, properties, body) =>
                {
                    CheckMessageType<TResponse>(properties);
                    var response = serializer.BytesToMessage<TResponse>(body);

                    var tcs = new TaskCompletionSource<object>();

                    try
                    {
                        onResponse(response);
                        tcs.SetResult(null);
                    }
                    catch (Exception exception)
                    {
                        tcs.SetException(exception);
                    }
                    return tcs.Task;
                });

            responseChannel.BasicConsume(
                respondQueue,           // queue
                NoAck,                  // noAck 
                consumer.ConsumerTag,   // consumerTag
                consumer);              // consumer
        }

        private void RequestPublish<TRequest>(TRequest request, string returnQueueName)
        {
            var requestTypeName = serializeType(typeof(TRequest));

            if (!threadLocalPublishChannel.IsValueCreated)
            {
                threadLocalPublishChannel.Value = connection.CreateModel();
                modelList.Add(threadLocalPublishChannel.Value);
            }

            // declare the exchange, binding and queue here. No need to set the mandatory flag, the recieving queue
            // will already have been declared, so in the case of no responder being present, message will collect
            // there.
            DeclareRequestResponseStructure(threadLocalPublishChannel.Value, requestTypeName);

            // tell the consumer to respond to the transient respondQueue
            var requestProperties = threadLocalPublishChannel.Value.CreateBasicProperties();
            requestProperties.ReplyTo = returnQueueName;
            requestProperties.Type = requestTypeName;

            var requestBody = serializer.MessageToBytes(request);
            threadLocalPublishChannel.Value.BasicPublish(
                RpcExchange,            // exchange 
                requestTypeName,        // routingKey 
                requestProperties,      // basicProperties 
                requestBody);           // body
        }

        public void Respond<TRequest, TResponse>(Func<TRequest, TResponse> responder)
        {
            if(responder == null)
            {
                throw new ArgumentNullException("responder");
            }

            Func<TRequest, Task<TResponse>> taskResponder = 
                request => Task<TResponse>.Factory.StartNew(_ => responder(request), null);

            RespondAsync(taskResponder);
        }

        public void RespondAsync<TRequest, TResponse>(Func<TRequest, Task<TResponse>> responder)
        {
            if (responder == null)
            {
                throw new ArgumentNullException("responder");
            }

            var requestTypeName = serializeType(typeof(TRequest));

            Action subscribeAction = () =>
            {
                var requestChannel = connection.CreateModel();
                modelList.Add( requestChannel );
                DeclareRequestResponseStructure(requestChannel, requestTypeName);

                var consumer = consumerFactory.CreateConsumer(requestChannel,
                    (consumerTag, deliveryTag, redelivered, exchange, routingKey, properties, body) =>
                    {
                        CheckMessageType<TRequest>(properties);
                        var request = serializer.BytesToMessage<TRequest>(body);
                        var responseTask = responder(request);

                        var tcs = new TaskCompletionSource<object>();
                        responseTask.ContinueWith(task =>
                        {
                            if (task.IsFaulted)
                            {
                                if (task.Exception != null)
                                {
                                    tcs.SetException(task.Exception);
                                }
                            }
                            else
                            {
                                // wait for the connection to come back
                                while (!connection.IsConnected) Thread.Sleep(100);

                                using(var responseChannel = connection.CreateModel())
                                {
                                    var responseProperties = responseChannel.CreateBasicProperties();
                                    responseProperties.Type = serializeType(typeof (TResponse));
                                    var responseBody = serializer.MessageToBytes(task.Result);

                                    responseChannel.BasicPublish(
                                        "",                 // exchange 
                                        properties.ReplyTo, // routingKey
                                        responseProperties, // basicProperties 
                                        responseBody);      // body
                                }
                                tcs.SetResult(null);
                            }
                        });
                        return tcs.Task;
                    });

                requestChannel.BasicConsume(
                    requestTypeName,        // queue 
                    NoAck,                   // noAck 
                    consumer.ConsumerTag,   // consumerTag
                    consumer);              // consumer
            };

            AddSubscriptionAction(subscribeAction);
        }

        public event Action Connected;

        protected void OnConnected()
        {
            if (Connected != null) Connected();

			logger.DebugWrite("Re-creating subscribers");
			foreach (var subscribeAction in subscribeActions)
			{
				subscribeAction();
			}
        }

        public event Action Disconnected;

        protected void OnDisconnected()
        {
            threadLocalPublishChannel.Dispose();
            threadLocalPublishChannel = new ThreadLocal<IModel>();

            publishExchanges.Clear();
            requestExchanges.Clear();
            responseQueueNameCache.Clear();
            if (Disconnected != null) Disconnected();
        }

        public bool IsConnected
        {
            get { return connection.IsConnected; }
        }

        private void DeclareRequestResponseStructure(IModel channel, string requestTypeName)
        {
            if (requestExchanges.Add(requestTypeName))
            {
                logger.DebugWrite("Declaring Request/Response structure for request: {0}", requestTypeName);

                channel.ExchangeDeclare(
                    RpcExchange, // exchange 
                    ExchangeType.Direct, // type 
                    false, // autoDelete 
                    true, // durable 
                    null); // arguments

                channel.QueueDeclare(
                    requestTypeName, // queue 
                    true, // durable 
                    false, // exclusive 
                    false, // autoDelete 
                    null); // arguments

                channel.QueueBind(
                    requestTypeName, // queue
                    RpcExchange, // exchange 
                    requestTypeName); // routingKey
            }
        }


    	public void AddSubscriptionAction(Action subscriptionAction)
		{
			subscribeActions.Add(subscriptionAction);

			try
			{
				subscriptionAction();
			}
			catch (OperationInterruptedException)
			{
			    
			}
            catch (EasyNetQException)
			{
				// Looks like the channel closed between our IsConnected check
				// and the subscription action. Do nothing here, when the 
				// connection comes back, the subcription action will be run then.
			}
		}

        private bool disposed = false;
        public void Dispose()
        {
            if (disposed) return;

            // Abort all Models
            if ( modelList.Count > 0 ) {
                foreach ( var model in modelList ) {
                    if ( null != model )
                        model.Abort();
                }
            }

            threadLocalPublishChannel.Dispose();
            consumerFactory.Dispose();
            connection.Dispose();

            disposed = true;
        }
    }
}