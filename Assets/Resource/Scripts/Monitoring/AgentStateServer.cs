// Other_Modules/AgentStateServer.cs
// Web 可视化仪表板的 HTTP 服务端。
// 主线程每帧采集快照 → 后台 HttpListener 线程提供 REST API。
using UnityEngine;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using Newtonsoft.Json;

// ─── AgentStateServer ────────────────────────────────────────────────────────

/// <summary>
/// 在 Unity 主线程采集 Agent 快照，通过后台 HttpListener 暴露 REST API。
/// 访问 http://localhost:{port}/ 打开仪表板。
/// </summary>
public partial class AgentStateServer : MonoBehaviour
{
    [Header("HTTP 配置")]
    public int port = 8765;

    [Header("刷新间隔（秒）")]
    [Min(0.1f)] public float snapshotInterval = 0.5f;

    // ─── 线程安全快照 ─────────────────────────────────────────
    private readonly object snapshotLock = new object();
    private string snapshotJson    = "[]";
    private string mapMetaJson     = "{}";
    private string messagesJson    = "[]";
    private string gridmapJson     = "{}";
    private string historyJson     = "{}";
    private string llmLogsJson     = "[]";
    private string whiteboardJson  = "{}";
    private string incidentsJson   = "[]";
    private bool   mapMetaReady    = false;
    private bool   gridmapReady    = false;

    // ─── Motion Events（静态缓冲，供 AgentMotionExecutor 主线程写入）──────────
    private static readonly object              motionEventLock   = new object();
    private static readonly Queue<MotionEventDto> motionEventBuffer = new Queue<MotionEventDto>();
    private const int MAX_MOTION_EVENTS = 200;
    private string motionEventsJson = "{}"; // { agentId: MotionEventDto[] }

    // ─── MAD 事件缓冲（静态，供 MADCoordinator 主线程推送）──────────────────
    private static readonly object                    madLock    = new object();
    private static readonly List<MadIncidentSnapshot> madBuffer  = new List<MadIncidentSnapshot>();
    private const int MAX_MAD_INCIDENTS = 50;

    // ─── Memory / Reflection 快照 ─────────────────────────────────────────────
    private string memoryJson = "[]";
    private string persistentMemJson = "{}";

    // ─── 涌现模块快照 ─────────────────────────────────────────────────────────
    private string emergenceJson = "[]";

    // ─── 命令队列（后台线程入队，主线程消费）────────────────────
    private readonly Queue<PendingCommand> commandQueue = new Queue<PendingCommand>();
    private readonly object commandLock = new object();

    // ─── 位置历史（主线程访问，无需加锁）────────────────────────
    private readonly Dictionary<string, Queue<float[]>> posHistory = new Dictionary<string, Queue<float[]>>();
    private const int MAX_HISTORY = 60;

    // ─── HTTP 监听 ────────────────────────────────────────────
    private HttpListener listener;
    private Thread       listenerThread;
    private bool         running;

    // ─── 采样计时 ────────────────────────────────────────────
    private float lastSnapshotTime;

    // ─── 内嵌 HTML ────────────────────────────────────────────
    private string dashboardHtml;

    // ─────────────────────────────────────────────────────────
    // Unity 生命周期
    // ─────────────────────────────────────────────────────────

    private void Start()
    {
        dashboardHtml = BuildDashboardHtml();
        StartHttpServer();
    }

    private void Update()
    {
        lock (commandLock)
        {
            while (commandQueue.Count > 0)
                ExecuteCommand(commandQueue.Dequeue());
        }

        if (Time.time - lastSnapshotTime >= snapshotInterval)
        {
            CaptureSnapshot();
            CaptureLlmLogs();
            CaptureWhiteboard();
            CaptureIncidents();
            CaptureMotionEvents();
            CaptureMemorySnapshots();
            CapturePersistentMemory();
            CaptureEmergence();
            lastSnapshotTime = Time.time;
        }
    }

    private void OnDestroy()
    {
        running = false;
        try { listener?.Stop(); } catch { }
        listenerThread?.Join(500);
    }

    // ─────────────────────────────────────────────────────────
    // 快照采集（主线程）
    // ─────────────────────────────────────────────────────────

    private void CaptureSnapshot()
    {
        // Agents
        var agents = FindObjectsOfType<IntelligentAgent>();
        var snapshots = new List<AgentStateSnapshot>(agents.Length);

        foreach (var a in agents)
        {
            if (a == null || a.Properties == null) continue;

            var adm  = a.GetComponent<ActionDecisionModule>();
            var plan = a.GetComponent<PlanningModule>();
            var pm   = a.GetComponent<PerceptionModule>();
            var ps   = a.GetComponent<PersonalitySystem>();

            AtomicAction curAction = adm?.GetCurrentAction();
            PlanStep     curStep   = plan?.GetCurrentStep();

            // 更新位置历史
            string aid = a.Properties.AgentID;
            if (!posHistory.TryGetValue(aid, out var queue))
            {
                queue = new Queue<float[]>(MAX_HISTORY);
                posHistory[aid] = queue;
            }
            queue.Enqueue(new float[] { a.transform.position.x, a.transform.position.y, a.transform.position.z });
            while (queue.Count > MAX_HISTORY) queue.Dequeue();

            // 获取 ctx 快照（ActionExecutionContext 全部字段）
            var ctxSnap = adm?.GetCtxSnapshot();

            // actionQueue → type 名数组
            string[] aqTypes = Array.Empty<string>();
            if (ctxSnap?.actionQueue != null)
            {
                aqTypes = new string[ctxSnap.actionQueue.Length];
                for (int j = 0; j < ctxSnap.actionQueue.Length; j++)
                    aqTypes[j] = ctxSnap.actionQueue[j]?.type.ToString() ?? "?";
            }

            // stepConstraints → 完整快照数组
            StructuredConstraintSnapshot[] scSnaps = Array.Empty<StructuredConstraintSnapshot>();
            if (ctxSnap?.stepConstraints != null)
            {
                scSnaps = new StructuredConstraintSnapshot[ctxSnap.stepConstraints.Length];
                for (int j = 0; j < ctxSnap.stepConstraints.Length; j++)
                {
                    var src = ctxSnap.stepConstraints[j];
                    scSnaps[j] = src == null ? new StructuredConstraintSnapshot() : new StructuredConstraintSnapshot
                    {
                        constraintId = src.constraintId,
                        cType        = src.cType,
                        channel      = src.channel,
                        groupScope   = src.groupScope,
                        subject      = src.subject,
                        targetObject = src.targetObject,
                        exclusive    = src.exclusive,
                        condition    = src.condition,
                        syncWith     = src.syncWith,
                        sign         = src.sign,
                        watchAgent   = src.watchAgent,
                        reactTo      = src.reactTo,
                    };
                }
            }

            var snap = new AgentStateSnapshot
            {
                agentId       = aid,
                type          = a.Properties.Type.ToString(),
                role          = a.Properties.Role.ToString(),
                teamId        = a.Properties.TeamID,
                position      = new float[] { a.transform.position.x, a.transform.position.y, a.transform.position.z },
                battery       = a.CurrentState?.BatteryLevel ?? 0f,
                admStatus     = adm != null ? adm.GetStatus().ToString() : "N/A",
                planState     = plan != null ? plan.state.ToString() : "N/A",
                currentAction = curAction?.type.ToString() ?? "None",
                currentTarget = curAction?.targetName ?? string.Empty,
                missionDesc   = plan != null ? plan.GetCurrentMissionDescription() : string.Empty,
                currentStep   = curStep?.text ?? string.Empty,
                recentEvents  = adm?.GetRecentEvents() ?? Array.Empty<string>(),
                nearbyAgentIds = pm?.nearbyAgents
                    ?.ConvertAll(go => go?.GetComponent<IntelligentAgent>()?.Properties?.AgentID ?? "?")
                    ?.ToArray() ?? Array.Empty<string>(),
                enemyAgentIds = pm?.enemyAgents
                    ?.ConvertAll(ea => ea?.Properties?.AgentID ?? "?")
                    ?.ToArray() ?? Array.Empty<string>(),
                perceptionRange = a.Properties.PerceptionRange,
                timestamp = Time.time,
                // ctx 全部字段
                msnId               = ctxSnap?.msnId ?? string.Empty,
                stepId              = ctxSnap?.stepId ?? string.Empty,
                stepText            = ctxSnap?.stepText ?? string.Empty,
                stepConstraints     = scSnaps,
                ctxStatus           = ctxSnap?.status.ToString() ?? string.Empty,
                iterationCount      = ctxSnap?.iterationCount ?? 0,
                currentLocationName = ctxSnap?.currentLocationName ?? string.Empty,
                executedActions     = ctxSnap?.executedActionsSummary?.ToArray() ?? Array.Empty<string>(),
                actionQueue         = aqTypes,
                currentActionIdx    = ctxSnap?.currentActionIdx ?? 0,
                isRollingMode       = ctxSnap?.isRollingMode ?? false,
                isAdversarial       = ps?.IsAdversarial ?? false,
                detectedObjects     = BuildDetectedObjectSnapshots(pm, a.transform.position),
            };
            snapshots.Add(snap);
        }

        // Map metadata（一次性构建，修正坐标原点）
        if (!mapMetaReady)
        {
            var grid = FindObjectOfType<CampusGrid2D>();
            if (grid != null)
            {
                mapMetaReady = true;

                // 修复：使用真实地图边界而非硬编码 0
                var features = new List<FeaturePoint>();
                if (grid.featureSpatialProfileByUid != null)
                {
                    foreach (var profile in grid.featureSpatialProfileByUid.Values)
                    {
                        features.Add(new FeaturePoint
                        {
                            name   = profile.name,
                            kind   = profile.kind,
                            x      = profile.centroidWorld.x,
                            z      = profile.centroidWorld.z,
                            radius = profile.footprintRadius
                        });
                    }
                }

                var meta = new MapMetadata
                {
                    originX    = grid.mapBoundsXY.xMin,
                    originZ    = grid.mapBoundsXY.yMin,
                    cellSize   = grid.cellSize,
                    gridWidth  = grid.gridWidth,
                    gridLength = grid.gridLength,
                    features   = features.ToArray()
                };
                var mj = JsonConvert.SerializeObject(meta);
                lock (snapshotLock) { mapMetaJson = mj; }

                // 构建格栅地图（一次性）
                BuildGridmap(grid);
            }
        }

        // 序列化位置历史
        var histDict = new Dictionary<string, float[][]>();
        foreach (var kv in posHistory)
            histDict[kv.Key] = kv.Value.ToArray();
        var hj = JsonConvert.SerializeObject(histDict);

        // Communication log
        string msgJson = "[]";
        var mgr = CommunicationManager.Instance;
        if (mgr != null)
        {
            var log = mgr.RecentMessageLog;
            var entries = new List<MessageLogEntry>(Mathf.Min(200, log.Count));
            int start = Mathf.Max(0, log.Count - 200);
            for (int i = log.Count - 1; i >= start; i--)
            {
                var msg = log[i];
                entries.Add(new MessageLogEntry
                {
                    sender    = msg.SenderID,
                    receiver  = msg.ReceiverID ?? msg.TargetAgentId ?? "—",
                    type      = msg.Type.ToString(),
                    timestamp = msg.Timestamp,
                    content   = msg.Content?.Length > 2000 ? msg.Content.Substring(0, 2000) + "…" : msg.Content
                });
            }
            msgJson = JsonConvert.SerializeObject(entries);
        }

        var snapshotJsonNew = JsonConvert.SerializeObject(snapshots);
        lock (snapshotLock)
        {
            snapshotJson = snapshotJsonNew;
            messagesJson = msgJson;
            historyJson  = hj;
        }
    }

    /// <summary>
    /// 将 PerceptionModule.detectedObjects 转换为仪表板快照数组。
    /// </summary>
    private static DetectedObjectSnapshot[] BuildDetectedObjectSnapshots(
        PerceptionModule pm, Vector3 agentPos)
    {
        if (pm == null || pm.detectedObjects == null || pm.detectedObjects.Count == 0)
            return Array.Empty<DetectedObjectSnapshot>();

        var list = pm.detectedObjects;
        var result = new DetectedObjectSnapshot[list.Count];
        for (int i = 0; i < list.Count; i++)
        {
            var node = list[i];
            float dist = Vector3.Distance(node.WorldPosition, agentPos);
            result[i] = new DetectedObjectSnapshot
            {
                nodeId   = node.NodeId,
                nodeType = node.NodeType.ToString(),
                position = new float[] { node.WorldPosition.x, node.WorldPosition.y, node.WorldPosition.z },
                isDynamic = node.IsDynamic,
                distance  = Mathf.Round(dist * 10f) / 10f
            };
        }
        return result;
    }

    // ─────────────────────────────────────────────────────────
    // Motion Event 静态队列（供 AgentMotionExecutor 主线程写入）
    // ─────────────────────────────────────────────────────────

    /// <summary>
    /// 供 AgentMotionExecutor 在协程中调用（Unity 主线程）。
    /// 将运动事件压入静态缓冲队列，下一次 CaptureMotionEvents() 时序列化。
    /// AgentStateServer 未初始化时事件也会安全积累。
    /// </summary>
    public static void PushMotionEvent(string agentId, string eventType, string message)
    {
        var ev = new MotionEventDto
        {
            agentId   = agentId,
            eventType = eventType,
            message   = message,
            timestamp = Time.time,
        };
        lock (motionEventLock)
        {
            motionEventBuffer.Enqueue(ev);
            while (motionEventBuffer.Count > MAX_MOTION_EVENTS)
                motionEventBuffer.Dequeue();
        }
    }

    /// <summary>
    /// 供 MADCoordinator 在协程中调用（Unity 主线程）。
    /// 以 incidentId 为键做 upsert，保持最新状态。
    /// </summary>
    public static void PushMadIncident(MadIncidentSnapshot snap)
    {
        if (snap == null) return;
        lock (madLock)
        {
            madBuffer.RemoveAll(i => i.incidentId == snap.incidentId);
            madBuffer.Add(snap);
            while (madBuffer.Count > MAX_MAD_INCIDENTS)
                madBuffer.RemoveAt(0);
        }
    }

    // ─────────────────────────────────────────────────────────
    // 运动事件快照采集（主线程）
    // ─────────────────────────────────────────────────────────

    private void CaptureMotionEvents()
    {
        MotionEventDto[] snapshot;
        lock (motionEventLock)
        {
            snapshot = motionEventBuffer.ToArray();
        }

        // 按 agentId 分组，每个 agent 取最新 5 条
        var grouped = new Dictionary<string, List<MotionEventDto>>();
        foreach (var ev in snapshot)
        {
            if (!grouped.TryGetValue(ev.agentId, out var list))
            {
                list = new List<MotionEventDto>();
                grouped[ev.agentId] = list;
            }
            list.Add(ev);
        }

        var result = new Dictionary<string, MotionEventDto[]>();
        foreach (var kv in grouped)
            result[kv.Key] = kv.Value.OrderByDescending(e => e.timestamp).Take(5).ToArray();

        var json = JsonConvert.SerializeObject(result);
        lock (snapshotLock) { motionEventsJson = json; }
    }

    // ─────────────────────────────────────────────────────────
    // 记忆 / 反思洞察快照采集（主线程）
    // ─────────────────────────────────────────────────────────

    private void CaptureMemorySnapshots()
    {
        var agents   = FindObjectsOfType<IntelligentAgent>();
        var payloads = new List<AgentMemoryPayload>(agents.Length);
        var now      = DateTime.UtcNow;

        foreach (var agent in agents)
        {
            if (agent == null || agent.Properties == null) continue;
            var mm = agent.GetComponent<MemoryModule>();
            if (mm == null) continue;

            string agentId = agent.Properties.AgentID;

            // ── 记忆：程序性提示（按 strengthScore 降序，最多5条）优先 ──────────
            var procedural = mm.memories
                .Where(m => m.isProceduralHint)
                .OrderByDescending(m => m.strengthScore)
                .Take(5)
                .ToList();
            var procIds = new HashSet<string>(procedural.Select(m => m.id));

            // 其余按 createdAt 降序取25条
            var recent = mm.memories
                .Where(m => !procIds.Contains(m.id))
                .OrderByDescending(m => m.createdAt)
                .Take(25)
                .ToList();

            // 合并，整体再按 createdAt 降序
            var combined = procedural.Concat(recent)
                .OrderByDescending(m => m.createdAt)
                .ToList();

            var memSnaps = combined.Select(m => new MemorySnapshot
            {
                id               = m.id,
                kind             = m.kind.ToString(),
                summary          = m.summary,
                detail           = (m.detail != null && m.detail.Length > 500)
                                   ? m.detail.Substring(0, 500) + "…"
                                   : m.detail,
                status           = m.status.ToString(),
                importance       = m.importance,
                confidence       = m.confidence,
                strengthScore    = m.strengthScore,
                isProceduralHint = m.isProceduralHint,
                reflectionDepth  = m.reflectionDepth,
                sourceModule     = m.sourceModule,
                missionId        = m.missionId,
                targetRef        = m.targetRef,
                outcome          = m.outcome,
                tags             = m.tags?.ToArray() ?? Array.Empty<string>(),
                createdAtUnix    = new DateTimeOffset(m.createdAt,   TimeSpan.Zero).ToUnixTimeSeconds(),
                lastAccessedAtUnix = new DateTimeOffset(m.lastAccessedAt, TimeSpan.Zero).ToUnixTimeSeconds(),
                accessCount      = m.accessCount,
            }).ToArray();

            // ── Insights：过滤已过期条目（expiresAt == MinValue 视为永久有效）──
            var validInsights = mm.reflectionInsights
                .Where(i => i.expiresAt == DateTime.MinValue || i.expiresAt > now)
                .OrderByDescending(i => i.createdAt)
                .ToList();

            var insightSnaps = validInsights.Select(i => new ReflectionInsightSnapshot
            {
                id                  = i.id,
                insightDepth        = i.insightDepth,
                title               = i.title,
                summary             = i.summary,
                applyWhen           = i.applyWhen,
                suggestedAdjustment = i.suggestedAdjustment,
                confidence          = i.confidence,
                missionId           = i.missionId,
                targetRef           = i.targetRef,
                tags                = i.tags?.ToArray() ?? Array.Empty<string>(),
                createdAtUnix       = new DateTimeOffset(i.createdAt, TimeSpan.Zero).ToUnixTimeSeconds(),
                expiresAtUnix       = i.expiresAt == DateTime.MinValue
                                      ? 0L
                                      : new DateTimeOffset(i.expiresAt, TimeSpan.Zero).ToUnixTimeSeconds(),
                remainingSeconds    = i.expiresAt == DateTime.MinValue
                                      ? -1f   // -1 表示永不过期
                                      : (float)(i.expiresAt - now).TotalSeconds,
            }).ToArray();

            payloads.Add(new AgentMemoryPayload
            {
                agentId          = agentId,
                totalMemoryCount = mm.memories.Count,
                memories         = memSnaps,
                insights         = insightSnaps,
            });
        }

        var json = JsonConvert.SerializeObject(payloads);
        lock (snapshotLock) { memoryJson = json; }
    }

    // ─────────────────────────────────────────────────────────
    // 持久化规律库快照采集（主线程）
    // 跨 agent 去重合并全部 Policy 记忆 + 有效 ReflectionInsight
    // ─────────────────────────────────────────────────────────

    private void CapturePersistentMemory()
    {
        var agents      = FindObjectsOfType<IntelligentAgent>();
        var now         = DateTime.UtcNow;
        var seenPol     = new HashSet<string>();
        var seenIns     = new HashSet<string>();
        var policyList  = new List<MemorySnapshot>();
        var insightList = new List<ReflectionInsightSnapshot>();

        foreach (var agent in agents)
        {
            var mm = agent?.GetComponent<MemoryModule>();
            if (mm == null) continue;

            foreach (var m in mm.memories)
            {
                if (m.kind != AgentMemoryKind.Policy || !seenPol.Add(m.id)) continue;
                policyList.Add(new MemorySnapshot
                {
                    id                 = m.id,
                    kind               = m.kind.ToString(),
                    summary            = m.summary,
                    detail             = (m.detail != null && m.detail.Length > 500)
                                        ? m.detail.Substring(0, 500) + "…" : m.detail,
                    status             = m.status.ToString(),
                    importance         = m.importance,
                    confidence         = m.confidence,
                    strengthScore      = m.strengthScore,
                    isProceduralHint   = m.isProceduralHint,
                    reflectionDepth    = m.reflectionDepth,
                    sourceModule       = m.sourceModule,
                    missionId          = m.missionId,
                    targetRef          = m.targetRef,
                    outcome            = m.outcome,
                    tags               = m.tags?.ToArray() ?? Array.Empty<string>(),
                    createdAtUnix      = new DateTimeOffset(m.createdAt, TimeSpan.Zero).ToUnixTimeSeconds(),
                    lastAccessedAtUnix = new DateTimeOffset(m.lastAccessedAt, TimeSpan.Zero).ToUnixTimeSeconds(),
                    accessCount        = m.accessCount,
                });
            }

            foreach (var i in mm.reflectionInsights)
            {
                if (i.expiresAt != DateTime.MinValue && i.expiresAt <= now) continue;
                if (!seenIns.Add(i.id)) continue;
                insightList.Add(new ReflectionInsightSnapshot
                {
                    id                  = i.id,
                    insightDepth        = i.insightDepth,
                    title               = i.title,
                    summary             = i.summary,
                    applyWhen           = i.applyWhen,
                    suggestedAdjustment = i.suggestedAdjustment,
                    confidence          = i.confidence,
                    missionId           = i.missionId,
                    targetRef           = i.targetRef,
                    tags                = i.tags?.ToArray() ?? Array.Empty<string>(),
                    createdAtUnix       = new DateTimeOffset(i.createdAt, TimeSpan.Zero).ToUnixTimeSeconds(),
                    expiresAtUnix       = i.expiresAt == DateTime.MinValue ? 0L
                                         : new DateTimeOffset(i.expiresAt, TimeSpan.Zero).ToUnixTimeSeconds(),
                    remainingSeconds    = i.expiresAt == DateTime.MinValue ? -1f
                                         : (float)(i.expiresAt - now).TotalSeconds,
                });
            }
        }

        policyList.Sort((a, b) => b.strengthScore.CompareTo(a.strengthScore));
        insightList.Sort((a, b) =>
        {
            int d = b.insightDepth.CompareTo(a.insightDepth);
            return d != 0 ? d : b.createdAtUnix.CompareTo(a.createdAtUnix);
        });

        var payload = new PersistentMemoryPayload
        {
            policyCount  = policyList.Count,
            insightCount = insightList.Count,
            policies     = policyList.ToArray(),
            insights     = insightList.ToArray(),
            saveFilePath = MemoryModule.SaveFilePath,
        };
        var json = JsonConvert.SerializeObject(payload);
        lock (snapshotLock) { persistentMemJson = json; }
    }

    // ─────────────────────────────────────────────────────────
    // 涌现模块快照采集（主线程）
    // ─────────────────────────────────────────────────────────

    private void CaptureEmergence()
    {
        var agents    = FindObjectsOfType<IntelligentAgent>();
        var snapshots = new List<EmergenceSnapshot>(agents.Length);

        foreach (var agent in agents)
        {
            if (agent == null || agent.Properties == null) continue;
            var adm  = agent.GetComponent<AutonomousDriveModule>();
            if (adm == null) continue;
            var ePlan = agent.GetComponent<PlanningModule>();
            var ePs   = agent.GetComponent<PersonalitySystem>();

            // 驱动力列表（降序）
            var drivesRaw = adm.LastDrives;
            var driveEntries = drivesRaw
                .OrderByDescending(kv => kv.Value)
                .Select(kv => new DriveEntry { name = kv.Key, strength = kv.Value })
                .ToArray();

            string topDriveName  = driveEntries.Length > 0 ? driveEntries[0].name     : string.Empty;
            float  topDriveStrength = driveEntries.Length > 0 ? driveEntries[0].strength : 0f;

            // 候选接受者
            var acceptorList = adm.PendingAcceptors;
            var acceptorEntries = new AcceptorEntry[acceptorList.Count];
            for (int i = 0; i < acceptorList.Count; i++)
            {
                acceptorEntries[i] = new AcceptorEntry
                {
                    agentId  = acceptorList[i].agentId,
                    battery  = acceptorList[i].battery,
                    location = acceptorList[i].location
                };
            }

            // 距下次评估的剩余秒数
            float secsLeft = agent.CurrentState?.Status != AgentStatus.Idle
                ? -1f
                : Mathf.Max(0f, adm.EvaluationInterval - (Time.time - adm.LastEvaluationTime));

            snapshots.Add(new EmergenceSnapshot
            {
                agentId             = agent.Properties.AgentID,
                isEvaluating        = adm.IsEvaluating,
                collectingAcceptors = adm.CollectingAcceptors,
                pendingAcceptors    = acceptorEntries,
                drives              = driveEntries,
                topDrive            = topDriveName,
                topDriveStrength    = topDriveStrength,
                lastGoal            = adm.LastGoal,
                lastThought         = adm.LastThought,
                lastNeedsHelp       = adm.LastNeedsHelp,
                secsUntilNextEval   = secsLeft,
                isAdversarial       = ePs?.IsAdversarial ?? false,
                isRunningSolo       = ePlan?.IsRunningSolo ?? false,
                inCollabSetup       = adm.IsEvaluating && (ePlan?.IsRunningSolo ?? false),
                lastSteps           = adm.LastSteps?.ToArray() ?? Array.Empty<string>(),
                currentStepIndex    = ePlan?.CurrentStepIndex ?? -1,
                totalStepCount      = ePlan?.TotalStepCount ?? 0,
            });
        }

        var json = JsonConvert.SerializeObject(snapshots);
        lock (snapshotLock) { emergenceJson = json; }
    }

    // ─────────────────────────────────────────────────────────
    // 紧急事件快照采集（主线程）
    // ─────────────────────────────────────────────────────────

    private void CaptureIncidents()
    {
        MadIncidentSnapshot[] snapshot;
        lock (madLock) { snapshot = madBuffer.ToArray(); }
        var json = JsonConvert.SerializeObject(snapshot);
        lock (snapshotLock) { incidentsJson = json; }
    }

    // ─────────────────────────────────────────────────────────
    // 格栅地图构建（一次性）
    // ─────────────────────────────────────────────────────────

    private void BuildGridmap(CampusGrid2D grid)
    {
        if (grid.cellTypeGrid == null) return;

        int outW = Mathf.Min(128, grid.gridWidth);
        int outH = Mathf.Min(128, grid.gridLength);
        int[][] map = new int[outH][];
        for (int z = 0; z < outH; z++)
        {
            map[z] = new int[outW];
            for (int x = 0; x < outW; x++)
            {
                int srcX = x * grid.gridWidth  / outW;
                int srcZ = z * grid.gridLength / outH;
                map[z][x] = (int)grid.cellTypeGrid[srcX, srcZ];
            }
        }

        var obj = new { w = outW, h = outH, cells = map };
        var gj = JsonConvert.SerializeObject(obj);
        lock (snapshotLock)
        {
            gridmapJson  = gj;
            gridmapReady = true;
        }
    }

    // ─────────────────────────────────────────────────────────
    // 命令执行（主线程）
    // ─────────────────────────────────────────────────────────

    private void ExecuteCommand(PendingCommand cmd)
    {
        if (cmd.type == CommandType.SubmitTask)
        {
            var obj = JsonConvert.DeserializeObject<TaskPayload>(cmd.payload);
            var planners = FindObjectsOfType<PlanningModule>();
            foreach (var p in planners)
            {
                if (p.state == PlanningState.Idle)
                {
                    int cnt = obj.agentCount > 0 ? obj.agentCount : FindObjectsOfType<IntelligentAgent>().Length;
                    p.SubmitMissionRequest(obj.mission, cnt);
                    break;
                }
            }
        }
        else if (cmd.type == CommandType.SetModel)
        {
            var obj = JsonConvert.DeserializeObject<ModelPayload>(cmd.payload);
            foreach (var iface in FindObjectsOfType<LLMInterface>())
                iface.SetModel(obj.model);
        }
    }

    // ─────────────────────────────────────────────────────────
    // LLM 日志采集（主线程）
    // ─────────────────────────────────────────────────────────

    private void CaptureLlmLogs()
    {
        var allInterfaces = FindObjectsOfType<LLMInterface>();
        var entries = new List<LlmLogEntry>();

        foreach (var iface in allInterfaces)
        {
            string ifaceId = iface.GetComponent<IntelligentAgent>()?.Properties?.AgentID ?? iface.gameObject.name;
            var logs = iface.LogEntries;
            foreach (var e in logs)
            {
                string content = e.content;
                if (content != null && content.Length > 3000)
                    content = content.Substring(0, 3000) + "…";
                entries.Add(new LlmLogEntry
                {
                    agentId     = !string.IsNullOrEmpty(e.agentId) ? e.agentId : ifaceId,
                    timestamp   = e.timestamp,
                    type        = e.type,
                    model       = e.model,
                    temperature = e.temperature,
                    maxTokens   = e.max_tokens,
                    content     = content,
                    tag         = e.callTag ?? string.Empty
                });
            }
        }

        // 取最新 300 条
        if (entries.Count > 300)
            entries = entries.GetRange(entries.Count - 300, 300);

        var json = JsonConvert.SerializeObject(entries);
        lock (snapshotLock) { llmLogsJson = json; }
    }

    // ─────────────────────────────────────────────────────────
    // 白板快照采集（主线程）
    // ─────────────────────────────────────────────────────────

    private void CaptureWhiteboard()
    {
        var wb = SharedWhiteboard.Instance;
        if (wb == null) return;

        var allGroups = wb.GetAllGroups();
        var entries   = new List<WhiteboardEntrySnapshot>();
        foreach (var kv in allGroups)
        {
            string gid = kv.Key;
            foreach (var e in kv.Value)
            {
                entries.Add(new WhiteboardEntrySnapshot
                {
                    groupId      = gid,
                    agentId      = e.agentId,
                    constraintId = e.constraintId,
                    entryType    = e.entryType.ToString(),
                    progress     = e.progress ?? string.Empty,
                    status       = e.status,
                    timestamp    = e.timestamp
                });
            }
        }

        var history = wb.GetWriteHistory();
        var histSnaps = new WhiteboardWriteRecord[history.Count];
        for (int i = 0; i < history.Count; i++)
            histSnaps[i] = history[i];

        var snap = new WhiteboardSnapshot { entries = entries.ToArray(), history = histSnaps };
        var json = JsonConvert.SerializeObject(snap);
        lock (snapshotLock) { whiteboardJson = json; }
    }

    // ─────────────────────────────────────────────────────────
    // HTTP 服务（后台线程）
    // ─────────────────────────────────────────────────────────

    private void StartHttpServer()
    {
        // 同时注册两个前缀，避免 Host header 不匹配导致 Bad Request
        string[] bothPrefixes = {
            $"http://127.0.0.1:{port}/",
            $"http://localhost:{port}/",
        };

        // 策略1：同时注册两个前缀（最佳，覆盖所有访问方式）
        try
        {
            listener = new HttpListener();
            listener.Prefixes.Clear();
            foreach (var p in bothPrefixes) listener.Prefixes.Add(p);
            listener.Start();
            running = true;
            listenerThread = new Thread(ListenLoop) { IsBackground = true, Name = "AgentStateServer" };
            listenerThread.Start();
            Debug.Log($"[AgentStateServer] 已启动，同时监听 127.0.0.1 和 localhost:{port}");
            return;
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[AgentStateServer] 双前缀启动失败: {e.Message}，回退到单前缀…");
            try { listener?.Stop(); } catch { }
            listener = null;
        }

        // 策略2：逐一尝试单前缀
        foreach (var prefix in bothPrefixes)
        {
            try
            {
                listener = new HttpListener();
                listener.Prefixes.Clear();
                listener.Prefixes.Add(prefix);
                listener.Start();
                running = true;
                listenerThread = new Thread(ListenLoop) { IsBackground = true, Name = "AgentStateServer" };
                listenerThread.Start();
                Debug.Log($"[AgentStateServer] 已启动：{prefix}");
                return;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[AgentStateServer] 前缀 {prefix} 启动失败: {e.Message}，尝试下一个…");
                try { listener?.Stop(); } catch { }
                listener = null;
            }
        }

        Debug.LogError($"[AgentStateServer] 所有前缀均启动失败。" +
            $"可尝试以管理员身份运行 Unity，或执行：\n" +
            $"  netsh http add urlacl url=http://127.0.0.1:{port}/ user=Everyone\n" +
            $"  netsh http add urlacl url=http://localhost:{port}/ user=Everyone");
    }

    private void ListenLoop()
    {
        while (running && listener != null && listener.IsListening)
        {
            try
            {
                var ctx  = listener.GetContext();
                var req  = ctx.Request;
                var resp = ctx.Response;

                string path = req.Url.AbsolutePath;

                byte[] body;
                string mime;

                switch (path)
                {
                    case "/api/state":
                        { string json; lock (snapshotLock) { json = snapshotJson; }
                          body = Encoding.UTF8.GetBytes(json); mime = "application/json"; }
                        break;

                    case "/api/map":
                        { string json; lock (snapshotLock) { json = mapMetaJson; }
                          body = Encoding.UTF8.GetBytes(json); mime = "application/json"; }
                        break;

                    case "/api/messages":
                        { string json; lock (snapshotLock) { json = messagesJson; }
                          body = Encoding.UTF8.GetBytes(json); mime = "application/json"; }
                        break;

                    case "/api/gridmap":
                        { string json; lock (snapshotLock) { json = gridmapJson; }
                          body = Encoding.UTF8.GetBytes(json); mime = "application/json"; }
                        break;

                    case "/api/history":
                        { string json; lock (snapshotLock) { json = historyJson; }
                          body = Encoding.UTF8.GetBytes(json); mime = "application/json"; }
                        break;

                    case "/api/llm-logs":
                        { string json; lock (snapshotLock) { json = llmLogsJson; }
                          body = Encoding.UTF8.GetBytes(json); mime = "application/json"; }
                        break;

                    case "/api/whiteboard":
                        { string json; lock (snapshotLock) { json = whiteboardJson; }
                          body = Encoding.UTF8.GetBytes(json); mime = "application/json"; }
                        break;

                    case "/api/incidents":
                        { string json; lock (snapshotLock) { json = incidentsJson; }
                          body = Encoding.UTF8.GetBytes(json); mime = "application/json"; }
                        break;

                    case "/api/motion-events":
                        { string json; lock (snapshotLock) { json = motionEventsJson; }
                          body = Encoding.UTF8.GetBytes(json); mime = "application/json"; }
                        break;

                    case "/api/memory":
                        { string json; lock (snapshotLock) { json = memoryJson; }
                          body = Encoding.UTF8.GetBytes(json); mime = "application/json"; }
                        break;

                    case "/api/memory/policies":
                        { string json; lock (snapshotLock) { json = persistentMemJson; }
                          body = Encoding.UTF8.GetBytes(json); mime = "application/json"; }
                        break;

                    case "/api/emergence":
                        { string json; lock (snapshotLock) { json = emergenceJson; }
                          body = Encoding.UTF8.GetBytes(json); mime = "application/json"; }
                        break;

                    case "/api/task":
                        if (req.HttpMethod == "OPTIONS")
                        {
                            body = new byte[0]; mime = "text/plain";
                        }
                        else
                        {
                            string taskBody;
                            using (var sr = new System.IO.StreamReader(req.InputStream, Encoding.UTF8))
                                taskBody = sr.ReadToEnd();
                            lock (commandLock)
                                commandQueue.Enqueue(new PendingCommand { type = CommandType.SubmitTask, payload = taskBody });
                            body = Encoding.UTF8.GetBytes("{\"ok\":true}"); mime = "application/json";
                        }
                        break;

                    case "/api/model":
                        if (req.HttpMethod == "OPTIONS")
                        {
                            body = new byte[0]; mime = "text/plain";
                        }
                        else
                        {
                            string modelBody;
                            using (var sr = new System.IO.StreamReader(req.InputStream, Encoding.UTF8))
                                modelBody = sr.ReadToEnd();
                            lock (commandLock)
                                commandQueue.Enqueue(new PendingCommand { type = CommandType.SetModel, payload = modelBody });
                            body = Encoding.UTF8.GetBytes("{\"ok\":true}"); mime = "application/json";
                        }
                        break;

                    default:
                        if (req.HttpMethod == "OPTIONS")
                        {
                            body = new byte[0]; mime = "text/plain";
                        }
                        else
                        {
                            body = Encoding.UTF8.GetBytes(dashboardHtml);
                            mime = "text/html; charset=utf-8";
                        }
                        break;
                }

                resp.ContentType     = mime;
                resp.ContentLength64 = body.Length;
                resp.Headers.Add("Access-Control-Allow-Origin", "*");
                resp.Headers.Add("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
                resp.Headers.Add("Access-Control-Allow-Headers", "Content-Type");
                if (req.HttpMethod == "OPTIONS")
                    resp.StatusCode = 200;
                resp.OutputStream.Write(body, 0, body.Length);
                resp.Close();
            }
            catch (Exception) when (!running)
            {
                break;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[AgentStateServer] 请求处理错误: {e.Message}");
            }
        }
    }

    // ─────────────────────────────────────────────────────────
    // 内嵌仪表板 HTML（读外部文件，失败时回退内嵌）
    // ─────────────────────────────────────────────────────────

    private string BuildDashboardHtml()
    {
        // Application.dataPath = <Project>/Assets
        // dashboard.html 位于 Assets/Resource/Scripts/web/dashboard.html
        string externalPath = System.IO.Path.Combine(
            Application.dataPath, "Resource", "Scripts", "web", "dashboard.html");
        if (System.IO.File.Exists(externalPath))
        {
            try { return System.IO.File.ReadAllText(externalPath, Encoding.UTF8); }
            catch { }
        }
        return EMBEDDED_HTML;
    }

    // 最小内嵌回退页（仅在无法读取外部文件时使用）
    private const string EMBEDDED_HTML = @"<!DOCTYPE html>
<html lang='zh-CN'><head><meta charset='UTF-8'><title>多智能体仿真仪表板</title>
<style>body{background:#0d1117;color:#c9d1d9;font-family:monospace;display:flex;justify-content:center;align-items:center;height:100vh;margin:0}</style>
</head><body>
<div style='text-align:center'>
  <h2 style='color:#58a6ff'>多智能体仿真仪表板</h2>
  <p style='color:#8b949e;margin-top:8px'>外部 web/dashboard.html 未找到，请将 dashboard.html 放置到项目 web/ 目录下。</p>
  <p style='color:#8b949e;margin-top:4px'>API 端点：<a href='/api/state' style='color:#58a6ff'>/api/state</a> ·
     <a href='/api/map' style='color:#58a6ff'>/api/map</a> ·
     <a href='/api/gridmap' style='color:#58a6ff'>/api/gridmap</a> ·
     <a href='/api/history' style='color:#58a6ff'>/api/history</a></p>
</div>
</body></html>";
}
