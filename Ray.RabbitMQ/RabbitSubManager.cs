﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Ray.Core.MQ;
using Ray.Core.Utils;

namespace Ray.RabbitMQ
{
    public class RabbitSubManager : SubManager
    {
        ILogger<RabbitSubManager> logger = default;
        IServiceProvider provider;
        IRabbitMQClient client;
        public RabbitSubManager(ILogger<RabbitSubManager> logger, IRabbitMQClient client, IServiceProvider provider)
        {
            this.client = client;
            this.logger = logger;
            this.provider = provider;
        }
        protected override async Task Start(List<SubAttribute> attributes, string node, List<string> nodeList = null)
        {
            var hash = nodeList == null ? null : new ConsistentHash(nodeList);
            var consumerList = new SortedList<int, ConsumerInfo>();
            var rd = new Random((int)DateTime.UtcNow.Ticks);
            foreach (var attribute in attributes)
            {
                if (attribute is RabbitSubAttribute subAttribute)
                {
                    subAttribute.Init(client);
                    for (int i = 0; i < subAttribute.QueueList.Count(); i++)
                    {
                        var queue = subAttribute.QueueList[i];
                        var hashNode = hash != null ? hash.GetNode(queue.Queue) : node;
                        if (node == hashNode)
                        {
                            consumerList.Add(rd.Next(), new ConsumerInfo()
                            {
                                Exchange = subAttribute.Exchange,
                                Queue = $"{ subAttribute.Group}_{queue.Queue}",
                                RoutingKey = queue.RoutingKey,
                                AutoAck = subAttribute.AutoAck,
                                Handler = (ISubHandler)provider.GetService(subAttribute.Handler)
                            });
                        }
                    }
                }
            }
            await Start(consumerList.Values.ToList());
        }

        List<ConsumerInfo> ConsumerList { get; set; }
        public async Task Start(List<ConsumerInfo> consumerList)
        {
            if (consumerList != null)
            {
                for (int i = 0; i < consumerList.Count; i++)
                {
                    var consumer = consumerList[i];
                    await StartSub(consumer);
                }
                ConsumerList = consumerList;
#pragma warning disable CS4014 // 由于此调用不会等待，因此在调用完成前将继续执行当前方法
                RestartMonitor();
#pragma warning restore CS4014 // 由于此调用不会等待，因此在调用完成前将继续执行当前方法
            }
        }
        private async Task RestartMonitor()
        {
            while (true)
            {
                await Task.Delay(10 * 1000);
                foreach (var consumer in ConsumerList)
                {
                    if (consumer.Channel.Model == null || consumer.Channel.Model.IsClosed)
                        await ReStart(consumer);
                }
            }
        }
        private async Task StartSub(ConsumerInfo consumer)
        {
            await InitModel(consumer);
            consumer.BasicConsumer = new EventingBasicConsumer(consumer.Channel.Model);
            consumer.BasicConsumer.Received += async (ch, ea) =>
            {
                try
                {
                    await consumer.Handler.Notice(ea.Body);
                    if (!consumer.AutoAck)
                    {
                        consumer.Channel.Model.BasicAck(ea.DeliveryTag, false);
                    }
                }
                catch (Exception exception)
                {
                    //需要记录错误日志
                    logger.LogError(exception.InnerException ?? exception, $"An error occurred in {consumer.Exchange}-{consumer.Queue}");
                    consumer.Channel.Model.Dispose();
                }
            };
            consumer.BasicConsumer.ConsumerTag = consumer.Channel.Model.BasicConsume(consumer.Queue, consumer.AutoAck, consumer.BasicConsumer);
        }
        private async Task InitModel(ConsumerInfo consumer)
        {
            if (consumer.Channel == default)
            {
                consumer.Channel = await client.PullModel();
            }
            else
            {
                if (consumer.Channel.Model.IsClosed)
                {
                    consumer.Channel.Dispose();
                    consumer.Channel = await client.PullModel();
                }
                else
                {
                    return;
                }
            }
            consumer.Channel.Model.ExchangeDeclare(consumer.Exchange, "direct", true);
            consumer.Channel.Model.QueueDeclare(consumer.Queue, true, false, false, null);
            consumer.Channel.Model.QueueBind(consumer.Queue, consumer.Exchange, consumer.RoutingKey);
        }
        /// <summary>
        /// 重启消费者
        /// </summary>
        /// <param name="consumer"></param>
        /// <returns></returns>
        public async Task ReStart(ConsumerInfo consumer)
        {
            if (consumer.Channel.Model.IsOpen)
            {
                consumer.Channel.Model.Dispose();
            }
            await StartSub(consumer);
        }
        public override void Stop()
        {
            if (ConsumerList != null)
            {
                foreach (var consumer in ConsumerList)
                {
                    if (consumer.Channel.Model.IsOpen)
                    {
                        consumer.Channel.Model.BasicCancel(consumer.BasicConsumer.ConsumerTag);
                        consumer.Channel.Dispose();
                    }
                }
            }
        }
    }
    public class ConsumerInfo
    {
        public string Exchange { get; set; }
        public string Queue { get; set; }
        public string RoutingKey { get; set; }
        public bool AutoAck { get; set; }
        public ISubHandler Handler { get; set; }
        public ModelWrapper Channel { get; set; }
        public EventingBasicConsumer BasicConsumer { get; set; }
    }
}
