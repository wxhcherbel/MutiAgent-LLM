// Other_Modules/AgentStateServer.cs
// Web 可视化仪表板的 HTTP 服务端。
// 主线程每帧采集快照 → 后台 HttpListener 线程提供 REST API。
using UnityEngine;
using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Threading;
using Newtonsoft.Json;

// ─── 数据结构 ─────────────────────────────────────────────────────────────────

[Serializable]
public class AgentStateSnapshot
{
    public string   agentId;
    public string   type;           // Quadcopter / WheeledRobot
    public string   role;
    public int      teamId;
    public float[]  position;       // [x, y, z]
    public float    battery;
    public string   admStatus;      // ADMStatus 枚举名
    public string   planState;      // PlanningState 枚举名
    public string   currentAction;  // 当前原子动作类型
    public string   currentTarget;  // 当前目标地名
    public string   missionDesc;    // 当前任务描述
    public string   currentStep;    // 当前步骤文本
    public string[] plannedTargets;
    public string[] recentEvents;
    public string[] nearbyAgentIds;
    public string[] enemyAgentIds;
    public float    timestamp;
}

[Serializable]
public class MapMetadata
{
    public float  originX;
    public float  originZ;
    public float  cellSize;
    public int    gridWidth;
    public int    gridLength;
    public FeaturePoint[] features;
}

[Serializable]
public class FeaturePoint
{
    public string name;
    public string kind;     // building / road / water / green 等
    public float  x;
    public float  z;
    public float  radius;   // footprintRadius（世界单位）
}

[Serializable]
public class MessageLogEntry
{
    public string sender;
    public string receiver;
    public string type;
    public float  timestamp;
    public string content;
}

[Serializable]
public class LlmLogEntry
{
    public string agentId;
    public string timestamp;
    public string type;        // "send" / "receive" / "error"
    public string model;
    public float  temperature;
    public int    maxTokens;
    public string content;     // 截断到 3000 字符
}

// ─── AgentStateServer ────────────────────────────────────────────────────────

/// <summary>
/// 在 Unity 主线程采集 Agent 快照，通过后台 HttpListener 暴露 REST API。
/// 访问 http://localhost:{port}/ 打开仪表板。
/// </summary>
public class AgentStateServer : MonoBehaviour
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
    private bool   mapMetaReady    = false;
    private bool   gridmapReady    = false;

    // ─── 命令队列（后台线程入队，主线程消费）────────────────────
    private readonly Queue<PendingCommand> commandQueue = new Queue<PendingCommand>();
    private readonly object commandLock = new object();
    private enum CommandType { SubmitTask, SetModel }
    private struct PendingCommand { public CommandType type; public string payload; }
    [Serializable] private class TaskPayload  { public string mission; public int agentCount = 1; }
    [Serializable] private class ModelPayload { public string model; }

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
                missionDesc   = plan?.currentMission?.missionDescription ?? string.Empty,
                currentStep   = curStep?.text ?? string.Empty,
                plannedTargets = Array.Empty<string>(),
                recentEvents  = Array.Empty<string>(),
                nearbyAgentIds = pm?.nearbyAgents
                    ?.ConvertAll(go => go?.GetComponent<IntelligentAgent>()?.Properties?.AgentID ?? "?")
                    ?.ToArray() ?? Array.Empty<string>(),
                enemyAgentIds = pm?.enemyAgents
                    ?.ConvertAll(ea => ea?.Properties?.AgentID ?? "?")
                    ?.ToArray() ?? Array.Empty<string>(),
                timestamp = Time.time
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
            string agentId = iface.GetComponent<IntelligentAgent>()?.Properties?.AgentID ?? iface.gameObject.name;
            var logs = iface.LogEntries;
            foreach (var e in logs)
            {
                string content = e.content;
                if (content != null && content.Length > 3000)
                    content = content.Substring(0, 3000) + "…";
                entries.Add(new LlmLogEntry
                {
                    agentId     = agentId,
                    timestamp   = e.timestamp,
                    type        = e.type,
                    model       = e.model,
                    temperature = e.temperature,
                    maxTokens   = e.max_tokens,
                    content     = content
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
    // HTTP 服务（后台线程）
    // ─────────────────────────────────────────────────────────

    private void StartHttpServer()
    {
        string[] prefixCandidates = {
            $"http://127.0.0.1:{port}/",
            $"http://localhost:{port}/",
        };

        foreach (var prefix in prefixCandidates)
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
                Debug.Log($"[AgentStateServer] 已启动：{prefix}  （浏览器访问 http://127.0.0.1:{port}/）");
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
            $"  netsh http add urlacl url=http://127.0.0.1:{port}/ user=Everyone");
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
