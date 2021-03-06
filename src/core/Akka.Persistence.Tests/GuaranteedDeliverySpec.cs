﻿using System;
using System.Collections.Generic;
using System.Linq;
using Akka.Actor;
using Akka.Event;
using Akka.TestKit;
using Xunit;

namespace Akka.Persistence.Tests
{
    public class GuaranteedDeliverySpec : PersistenceSpec
    {

        #region internal test classes

        class Sender : GuaranteedDeliveryActor
        {
            private readonly ActorRef _testActor;
            private readonly string _name;
            private readonly bool _isAsync;
            private readonly IDictionary<string, string> _destinations;
            private readonly LoggingAdapter _log;
            private ActorRef _lastSnapshotAskedForBy;

            public Sender(ActorRef testActor, string name, TimeSpan redeliverInterval, int warn, bool isAsync, IDictionary<string, string> destinations)
                : base()
            {
                _testActor = testActor;
                _name = name;
                _isAsync = isAsync;
                _destinations = destinations;
                _log = Context.GetLogger();
            }

            public override string PersistenceId { get { return _name; } }

            protected override bool ReceiveRecover(object message)
            {
                return message.Match()
                    .With<IEvt>(UpdateState)
                    .With<SnapshotOffer>(o =>
                    {
                        var snap = (Snap)o.Snapshot;
                        SetDeliverySnapshot(snap.DeliverySnapshot);
                    })
                    .WasHandled;
            }

            protected override bool ReceiveCommand(object message)
            {
                return message.Match()
                    .With<Req>(req =>
                    {
                        if (string.IsNullOrEmpty(req.Payload)) Sender.Tell(InvalidReq.Instance);
                        else
                        {
                            var c = char.ToUpper(req.Payload[0]);
                            var destination = _destinations[c.ToString()];
                            if (_isAsync) PersistAsync(new AcceptedReq(req.Payload, destination.ToString()), e =>
                            {
                                UpdateState(e);
                                Sender.Tell(ReqAck.Instance);
                            });
                            else Persist(new AcceptedReq(req.Payload, destination.ToString()), e =>
                            {
                                UpdateState(e);
                                Sender.Tell(ReqAck.Instance);
                            });
                        }
                    })
                    .With<ActionAck>(ack =>
                    {
                        _log.Debug("Sender got ack: {0}", ack.Id);
                        if (ConfirmDelivery(ack.Id))
                        {
                            if (_isAsync) PersistAsync(new ReqDone(ack.Id), done => UpdateState(done));
                            else Persist(new ReqDone(ack.Id), done => UpdateState(done));
                        }
                    })
                    .With<Boom>(boom =>
                    {
                        _log.Debug("Boom!");
                        throw new Exception("boom");
                    })
                    .With<SaveSnap>(save =>
                    {
                        _log.Debug("Save snapshot");
                        _lastSnapshotAskedForBy = Sender;
                        SaveSnapshot(new Snap(GetDeliverySnapshot()));
                    })
                    .With<SaveSnapshotSuccess>(succ =>
                    {
                        _log.Debug("Save snapshot success!");
                        if (_lastSnapshotAskedForBy != null)
                            _lastSnapshotAskedForBy.Tell(succ);
                    })
                    .With<UnconfirmedWarning>(warn =>
                    {
                        _log.Debug("Sender got unconfirmed warning: unconfirmed deliveries count {0}", warn.UnconfirmedDeliveries.Count());
                        _testActor.Tell(warn);
                    })
                    .WasHandled;
            }

            private void UpdateState(IEvt evt)
            {
                evt.Match()
                    .With<AcceptedReq>(a =>
                    {
                        _log.Debug("Deliver(destination, deliveryId => Action(deliveryId, {0})), recovering: {1}", a.Payload, IsRecovering);
                        Deliver(ActorPath.Parse(a.DestinationPath), deliveryId => new Action(deliveryId, a.Payload));
                    })
                    .With<ReqDone>(r =>
                    {
                        _log.Debug("ConfirmDelivery({0}), recovering: {1}", r.Id, IsRecovering);
                        ConfirmDelivery(r.Id);
                    });
            }
        }

        class Destination : ReceiveActor
        {
            private readonly ISet<long> _allReceived;

            public Destination(ActorRef testActor)
            {
                _allReceived = new HashSet<long>();
                Receive<Action>(a =>
                {
                    if (!_allReceived.Contains(a.Id))
                    {
                        testActor.Tell(a);
                        _allReceived.Add(a.Id);
                    }
                    this.Sender.Tell(new ActionAck(a.Id));
                });
            }
        }

        class Unreliable : ReceiveActor
        {
            private int _count = 0;
            public Unreliable(int dropMod, ActorRef target)
            {
                Receive<object>((message) =>
                {
                    _count++;
                    if (_count % dropMod != 0)
                    {
                        target.Forward(message);
                    }
                    return true;
                });
            }
        }

        [Serializable]
        sealed class Req
        {
            public Req(string payload)
            {
                Payload = payload;
            }

            public string Payload { get; private set; }
        }

        [Serializable]
        sealed class ReqAck
        {
            public static readonly ReqAck Instance = new ReqAck();
            private ReqAck() { }
            public override bool Equals(object obj)
            {
                return obj is ReqAck;
            }
        }

        [Serializable]
        sealed class InvalidReq
        {
            public static readonly InvalidReq Instance = new InvalidReq();
            private InvalidReq() { }
            public override bool Equals(object obj)
            {
                return obj is InvalidReq;
            }
        }

        interface IEvt { }

        [Serializable]
        sealed class AcceptedReq : IEvt
        {
            public AcceptedReq(string payload, string destinationPath)
            {
                Payload = payload;
                DestinationPath = destinationPath;
            }

            public string Payload { get; private set; }

            //FIXME: change to Akka.Actor.ActorPath when serialization problems will be solved
            public string DestinationPath { get; private set; }
        }

        [Serializable]
        sealed class ReqDone : IEvt, IEquatable<ReqDone>
        {
            public ReqDone(long id)
            {
                Id = id;
            }

            public long Id { get; private set; }
            public bool Equals(ReqDone other)
            {
                return other != null && other.Id == Id;
            }
        }

        [Serializable]
        sealed class Action : IEquatable<Action>
        {

            public Action(long id, string payload)
            {
                Id = id;
                Payload = payload;
            }

            public long Id { get; private set; }
            public string Payload { get; private set; }

            public override bool Equals(object obj)
            {
                if (ReferenceEquals(null, obj)) return false;
                return obj is Action && Equals((Action)obj);
            }
            public bool Equals(Action other)
            {
                return Id == other.Id && string.Equals(Payload, other.Payload);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    return (Id.GetHashCode() * 397) ^ (Payload != null ? Payload.GetHashCode() : 0);
                }
            }
        }

        [Serializable]
        sealed class ActionAck : IEquatable<ActionAck>
        {
            public ActionAck(long id)
            {
                Id = id;
            }

            public long Id { get; private set; }
            public bool Equals(ActionAck other)
            {
                return other != null && other.Id == Id;
            }
        }

        [Serializable]
        sealed class Boom { public static readonly Boom Instance = new Boom(); }

        [Serializable]
        sealed class SaveSnap { public static readonly SaveSnap Instance = new SaveSnap(); }

        [Serializable]
        sealed class Snap
        {
            public Snap(GuaranteedDeliverySnapshot deliverySnapshot)
            {
                DeliverySnapshot = deliverySnapshot;
            }

            public GuaranteedDeliverySnapshot DeliverySnapshot { get; private set; }
        }

        #endregion

        public GuaranteedDeliverySpec()
            : base(PersistenceSpec.Configuration("inmem", "GuaranteedDeliverySpec"))
        {
        }

        [Fact]
        public void GuaranteedDelivery_must_deliver_messages_in_order_when_nothing_is_lost()
        {
            var probe = CreateTestProbe();
            var destinations = new Dictionary<string, string> { { "A", Sys.ActorOf(Props.Create(() => new Destination(probe.Ref))).Path.ToString() } };
            var sender = Sys.ActorOf(Props.Create(() => new Sender(TestActor, Name, TimeSpan.FromMilliseconds(500), 5, false, destinations)), Name);

            sender.Tell(new Req("a"));
            ExpectMsg(ReqAck.Instance);
            probe.ExpectMsg<Action>(a => a.Id == 1 && a.Payload == "a");
            probe.ExpectNoMsg(TimeSpan.FromSeconds(1));
        }

        [Fact]
        public void GuaranteedDelivery_must_redeliver_lost_messages()
        {
            var probe = CreateTestProbe();
            var dest = Sys.ActorOf(Props.Create(() => new Destination(probe.Ref)));
            var destinations = new Dictionary<string, string> { { "A", Sys.ActorOf(Props.Create(() => new Unreliable(3, dest))).Path.ToString() } };
            var sender = Sys.ActorOf(Props.Create(() => new Sender(TestActor, Name, TimeSpan.FromMilliseconds(500), 5, false, destinations)), Name);

            sender.Tell(new Req("a-1"));
            ExpectMsg(ReqAck.Instance);
            probe.ExpectMsg<Action>(a => a.Id == 1 && a.Payload == "a-1");

            sender.Tell(new Req("a-2"));
            ExpectMsg(ReqAck.Instance);
            probe.ExpectMsg<Action>(a => a.Id == 2 && a.Payload == "a-2");

            sender.Tell(new Req("a-3"));
            sender.Tell(new Req("a-4"));
            ExpectMsg(ReqAck.Instance);
            ExpectMsg(ReqAck.Instance);
            // a-3 was lost ...
            probe.ExpectMsg<Action>(a => a.Id == 4 && a.Payload == "a-4");
            // ... and then redelivered
            probe.ExpectMsg<Action>(a => a.Id == 3 && a.Payload == "a-3");
            probe.ExpectNoMsg(TimeSpan.FromSeconds(1));
        }

        [Fact]
        public void GuaranteedDelivery_must_redeliver_lost_messages_after_restart()
        {
            var probe = CreateTestProbe();
            var dest = Sys.ActorOf(Props.Create(() => new Destination(probe.Ref)));
            var destinations = new Dictionary<string, string> { { "A", Sys.ActorOf(Props.Create(() => new Unreliable(3, dest))).Path.ToString() } };
            var sender = Sys.ActorOf(Props.Create(() => new Sender(TestActor, Name, TimeSpan.FromMilliseconds(500), 5, false, destinations)), Name);

            sender.Tell(new Req("a-1"));
            ExpectMsg(ReqAck.Instance);
            probe.ExpectMsg<Action>(a => a.Id == 1 && a.Payload == "a-1");

            sender.Tell(new Req("a-2"));
            ExpectMsg(ReqAck.Instance);
            probe.ExpectMsg<Action>(a => a.Id == 2 && a.Payload == "a-2");

            sender.Tell(new Req("a-3"));
            sender.Tell(new Req("a-4"));
            ExpectMsg(ReqAck.Instance);
            ExpectMsg(ReqAck.Instance);
            // a-3 was lost ...
            probe.ExpectMsg<Action>(a => a.Id == 4 && a.Payload == "a-4");
            // ... trigger restart ...
            sender.Tell(Boom.Instance);
            // ... and then redeliver
            probe.ExpectMsg<Action>(a => a.Id == 3 && a.Payload == "a-3");

            sender.Tell(new Req("a-5"));
            ExpectMsg(ReqAck.Instance);
            probe.ExpectMsg<Action>(a => a.Id == 5 && a.Payload == "a-5");

            probe.ExpectNoMsg(TimeSpan.FromSeconds(1));
        }

        [Fact]
        public void GuaranteedDelivery_must_resend_replayed_deliveries_with_an_initially_in_order_strategy_before_delivering_fresh_messages()
        {
            var probe = CreateTestProbe();
            var dest = Sys.ActorOf(Props.Create(() => new Destination(probe.Ref)));
            var destinations = new Dictionary<string, string> { { "A", Sys.ActorOf(Props.Create(() => new Unreliable(2, dest))).Path.ToString() } };
            var sender = Sys.ActorOf(Props.Create(() => new Sender(TestActor, Name, TimeSpan.FromMilliseconds(500), 5, false, destinations)), Name);

            sender.Tell(new Req("a-1"));
            ExpectMsg(ReqAck.Instance);
            probe.ExpectMsg<Action>(a => a.Id == 1 && a.Payload == "a-1");

            sender.Tell(new Req("a-2"));
            ExpectMsg(ReqAck.Instance);
            // a-2 was lost

            sender.Tell(new Req("a-3"));
            ExpectMsg(ReqAck.Instance);
            probe.ExpectMsg<Action>(a => a.Id == 3 && a.Payload == "a-3");

            sender.Tell(new Req("a-4"));
            ExpectMsg(ReqAck.Instance);
            // a-4 was lost

            // trigger restart
            sender.Tell(Boom.Instance);
            sender.Tell(new Req("a-5"));
            ExpectMsg(ReqAck.Instance);

            // and redeliver
            probe.ExpectMsg<Action>(a => a.Id == 2 && a.Payload == "a-2");      // redelivered
            // a-4 was redelivered but lost again
            probe.ExpectMsg<Action>(a => a.Id == 5 && a.Payload == "a-5");      // redelivered
            //FIXME: expression below works, just for some reason won't fit in 10 sec. interval
            probe.ExpectMsg<Action>(a => a.Id == 4 && a.Payload == "a-4", timeout: TimeSpan.FromSeconds(20));      // redelivered, 3th time

            probe.ExpectNoMsg(TimeSpan.FromSeconds(1));
        }

        [Fact(Skip = "FIXME")]
        public void GuaranteedDelivery_must_restore_state_from_snapshot()
        {
            var probe = CreateTestProbe();
            var dest = Sys.ActorOf(Props.Create(() => new Destination(probe.Ref)));
            var destinations = new Dictionary<string, string> { { "A", Sys.ActorOf(Props.Create(() => new Unreliable(3, dest))).Path.ToString() } };
            var sender = Sys.ActorOf(Props.Create(() => new Sender(TestActor, Name, TimeSpan.FromMilliseconds(500), 5, false, destinations)), Name);

            sender.Tell(new Req("a-1"));
            ExpectMsg(ReqAck.Instance);
            probe.ExpectMsg<Action>(a => a.Id == 1 && a.Payload == "a-1");

            sender.Tell(new Req("a-2"));
            ExpectMsg(ReqAck.Instance);
            probe.ExpectMsg<Action>(a => a.Id == 2 && a.Payload == "a-2");

            sender.Tell(new Req("a-3"));
            sender.Tell(new Req("a-4"));
            sender.Tell(SaveSnap.Instance);
            ExpectMsg(ReqAck.Instance);
            ExpectMsg(ReqAck.Instance);
            // a-3 was lost

            probe.ExpectMsg<Action>(a => a.Id == 4 && a.Payload == "a-4");

            //FIXME: must have serializable ActorPath to work (github issue: #553)
            // after snapshot succeed
            ExpectMsg<SaveSnapshotSuccess>();
            // trigger restart
            sender.Tell(Boom.Instance);
            // and then redelivered
            probe.ExpectMsg<Action>(a => a.Id == 3 && a.Payload == "a-3");

            sender.Tell(new Req("a-5"));
            ExpectMsg(ReqAck.Instance);
            probe.ExpectMsg<Action>(a => a.Id == 5 && a.Payload == "a-5");

            probe.ExpectNoMsg(TimeSpan.FromSeconds(1));
        }

        [Fact(Skip = "FIXME")]
        public void GuaranteedDelivery_must_warn_about_unconfirmed_messages()
        {
            var probeA = CreateTestProbe();
            var probeB = CreateTestProbe();

            var destinations = new Dictionary<string, string> { { "A", probeA.Ref.Path.ToString() }, { "B", probeB.Ref.Path.ToString() } };
            var sender = Sys.ActorOf(Props.Create(() => new Sender(TestActor, Name, TimeSpan.FromMilliseconds(500), 3, false, destinations)), Name);

            sender.Tell(new Req("a-1"));
            sender.Tell(new Req("b-1"));
            sender.Tell(new Req("b-2"));
            ExpectMsg(ReqAck.Instance);
            ExpectMsg(ReqAck.Instance);
            ExpectMsg(ReqAck.Instance);

            var unconfirmed = ReceiveWhile(TimeSpan.FromSeconds(3), x =>
                x is UnconfirmedWarning ? ((UnconfirmedWarning)x).UnconfirmedDeliveries : Enumerable.Empty<UnconfirmedDelivery>())
                .SelectMany(e => e).ToArray();

            var resultDestinations = unconfirmed.Select(x => x.Destination.ToString()).Distinct().ToArray();
            resultDestinations.ShouldOnlyContainInOrder(probeA.Ref.Path.ToString(), probeB.Ref.Path.ToString());
            var resultMessages = unconfirmed.Select(x => x.Message).Distinct().ToArray();
            resultMessages.ShouldOnlyContainInOrder(new Action(1, "a-1"), new Action(2, "b-1"), new Action(3, "b-2"));

            Sys.Stop(sender);
        }

        [Fact(Skip = "FIXME")]
        public void GuaranteedDelivery_must_redeliver_many_lost_messages()
        {
            var probeA = CreateTestProbe();
            var probeB = CreateTestProbe();
            var probeC = CreateTestProbe();
            var destA = Sys.ActorOf(Props.Create(() => new Destination(probeA.Ref)), "destination-a");
            var destB = Sys.ActorOf(Props.Create(() => new Destination(probeB.Ref)), "destination-b");
            var destC = Sys.ActorOf(Props.Create(() => new Destination(probeC.Ref)), "destination-c");
            var destinations = new Dictionary<string, string>
            {
                { "A", Sys.ActorOf(Props.Create(() => new Unreliable(2, destA)), "unreliable-a").Path.ToString() },
                { "B", Sys.ActorOf(Props.Create(() => new Unreliable(5, destB)), "unreliable-b").Path.ToString() },
                { "C", Sys.ActorOf(Props.Create(() => new Unreliable(3, destC)), "unreliable-c").Path.ToString() }
            };
            var sender = Sys.ActorOf(Props.Create(() => new Sender(TestActor, Name, TimeSpan.FromMilliseconds(500), 3, false, destinations)), Name);

            const int n = 100;
            var a = new string[n];
            var b = new string[n];
            var c = new string[n];
            for (int i = 0; i < n; i++)
            {
                var x = "a-" + i;
                a[i] = x;
                sender.Tell(new Req(x));
            }
            for (int i = 0; i < n; i++)
            {
                var x = "a-" + i;
                b[i] = x;
                sender.Tell(new Req("b-" + i));
            }
            for (int i = 0; i < n; i++)
            {
                var x = "a-" + i;
                c[i] = x;
                sender.Tell(new Req("c-" + i));
            }
            var deliverWithin = TimeSpan.FromSeconds(30);
            var resA = probeA.ReceiveN(n, deliverWithin).Cast<Action>().Select(x => x.Payload).Distinct().ToArray();
            var resB = probeB.ReceiveN(n, deliverWithin).Cast<Action>().Select(x => x.Payload).Distinct().ToArray();
            var resC = probeC.ReceiveN(n, deliverWithin).Cast<Action>().Select(x => x.Payload).Distinct().ToArray();

            resA.ShouldOnlyContainInOrder(a);
            resB.ShouldOnlyContainInOrder(b);
            resC.ShouldOnlyContainInOrder(c);
        }
    }
}