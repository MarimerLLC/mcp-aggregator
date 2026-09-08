### Run: `openai/gpt-4.1-nano` via openai (https://openrouter.ai/api/v1), thinking off, 10 runs/task, 2026-09-08 20:55 UTC
Model info: `{"id":"openai/gpt-4.1-nano","canonical_slug":"openai/gpt-4.1-nano-2025-04-14","hugging_face_id":"","name":"OpenAI: GPT-4.1 Nano","created":1744651369,"description":"For tasks that demand low latency, GPT‑4.1 nano is the fastest and cheapest model in the GPT-4.1 series. It delivers exceptional performance at a small size with its 1 million...","context_length":1047576,"architecture":{"modality":"text+image+file->text","input_modalities":["image","text","file"],"output_modalities":["text"],"tokenizer":"GPT","instruct_type":null},"pricing":{"prompt":"0.0000001","completion":"0.0000004","web_search":"0.01","input_cache_read":"0.000000025"},"top_provider":{"context_length":1047576,"max_completion_tokens":32768,"is_moderated":true},"per_request_limits":null,"supported_parameters":["max_completion_tokens","max_tokens","response_format","seed","structured_outputs","temperature","tool_choice","tools","top_p"],"default_parameters":{},"supported_voices":null,"knowledge_cutoff":"2024-06-30","expiration_date":null,"links":{"details":"/api/v1/models/openai/gpt-4.1-nano-2025-04-14/endpoints"},"benchmarks":{"design_arena":[{"arena":"models","category":"3d","elo":957,"win_rate":46,"rank":106},{"arena":"models","category":"codecategories","elo":980,"win_rate":47.3,"rank":113},{"arena":"models","category":"dataviz","elo":907,"win_rate":41.1,"rank":116},{"arena":"models","category":"gamedev","elo":996,"win_rate":49.6,"rank":109},{"arena":"models","category":"uicomponent","elo":934,"win_rate":43.9,"rank":111},{"arena":"models","category":"website","elo":987,"win_rate":48.1,"rank":115}],"artificial_analysis":{"intelligence_index":null,"coding_index":11.1,"agentic_index":null}}}`

#### Reliability by condition and task

| Condition | Task | Runs | First-call success | Completed | Wrong tool first | Avg steps to done | Avg turns | Avg input tok | Avg output tok | Errors |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| InvokeTool | calendar_tomorrow | 10 | 3/10 (30%) | 3/10 (30%) | 3 | 4.7 | 7.0 | 21476 | 150 | 0 |
| InvokeTool | docs_search | 10 | 2/10 (20%) | 6/10 (60%) | 1 | 5.2 | 5.7 | 13805 | 164 | 0 |
| InvokeTool | list_files_marimer | 10 | 1/10 (10%) | 4/10 (40%) | 0 | 4.3 | 6.4 | 23305 | 125 | 0 |
| InvokeTool | list_files_personal | 10 | 0/10 (0%) | 0/10 (0%) | 0 | - | 7.3 | 32389 | 100 | 0 |
| InvokeTool | send_email | 10 | 1/10 (10%) | 10/10 (100%) | 7 | 4.6 | 4.6 | 12226 | 164 | 0 |

#### Reliability by condition (all tasks)

| Condition | Runs | First-call success | Completed | Avg input tok / run | Avg output tok / run | Avg ms / run |
|---|---:|---:|---:|---:|---:|---:|
| InvokeTool | 50 | 7/50 (14%) | 23/50 (46%) | 20640 | 141 | 7221 |

#### tools/list size by condition

| Condition | Tools at start | Bytes at start | Tools at end | Bytes at end |
|---|---:|---:|---:|---:|
| InvokeTool | 13 | 7250 | 13 | 7250 |

#### First-attempt failures (what the model actually sent)

| Condition | Task | Run | First downstream call | Result |
|---|---|---:|---|---|
| InvokeTool | send_email | 1 | `invoke_tool({"serverName":"adjutant","toolName":"send_email","arguments":"{\u0022to\u0022: \u0022alice@example.com\u0022, \u0022subject\u0022: \u0022Lun…)` | error: An error occurred invoking 'send_email'. Argument mismatch for tool 'send_email' on 'adjutant'. Every key matched the schema, so a supplied … |
| InvokeTool | send_email | 2 | `invoke_tool({"serverName":"adjutant","toolName":"adjutant__send_email","arguments":"{\u0022to\u0022: [\u0022alice@example.com\u0022], \u0022subject\u002…)` | error: Unknown tool 'adjutant__send_email' on server 'adjutant'. 'adjutant__send_email' is the typed tool name; call it directly as a tool, or pass… |
| InvokeTool | send_email | 3 | `invoke_tool({"serverName":"adjutant","toolName":"adjutant__send_email","arguments":"{\u0022to\u0022: [\u0022alice@example.com\u0022], \u0022subject\u002…)` | error: Unknown tool 'adjutant__send_email' on server 'adjutant'. 'adjutant__send_email' is the typed tool name; call it directly as a tool, or pass… |
| InvokeTool | send_email | 4 | `invoke_tool({"serverName":"adjutant","toolName":"adjutant__send_email","arguments":"{\u0022to\u0022: [\u0022alice@example.com\u0022], \u0022subject\u002…)` | error: Unknown tool 'adjutant__send_email' on server 'adjutant'. 'adjutant__send_email' is the typed tool name; call it directly as a tool, or pass… |
| InvokeTool | send_email | 5 | `invoke_tool({"serverName":"adjutant","toolName":"adjutant__send_email","arguments":"{\u0022to\u0022: [\u0022alice@example.com\u0022], \u0022subject\u002…)` | error: Unknown tool 'adjutant__send_email' on server 'adjutant'. 'adjutant__send_email' is the typed tool name; call it directly as a tool, or pass… |
| InvokeTool | send_email | 6 | `invoke_tool({"serverName":"adjutant","toolName":"adjutant__send_email","arguments":"{\u0022to\u0022:[\u0022alice@example.com\u0022],\u0022subject\u0022:…)` | error: Unknown tool 'adjutant__send_email' on server 'adjutant'. 'adjutant__send_email' is the typed tool name; call it directly as a tool, or pass… |
| InvokeTool | send_email | 8 | `invoke_tool({"serverName":"adjutant","toolName":"send_email","arguments":"{\u0022to\u0022: \u0022alice@example.com\u0022, \u0022subject\u0022: \u0022Lun…)` | error: An error occurred invoking 'send_email'. Argument mismatch for tool 'send_email' on 'adjutant'. Every key matched the schema, so a supplied … |
| InvokeTool | send_email | 9 | `invoke_tool({"serverName":"adjutant","toolName":"adjutant__send_email","arguments":"{\u0022to\u0022: [\u0022alice@example.com\u0022], \u0022subject\u002…)` | error: Unknown tool 'adjutant__send_email' on server 'adjutant'. 'adjutant__send_email' is the typed tool name; call it directly as a tool, or pass… |
| InvokeTool | send_email | 10 | `invoke_tool({"serverName":"adjutant","toolName":"adjutant__send_email","arguments":"{\u0022to\u0022:[\u0022alice@example.com\u0022],\u0022subject\u0022:…)` | error: Unknown tool 'adjutant__send_email' on server 'adjutant'. 'adjutant__send_email' is the typed tool name; call it directly as a tool, or pass… |
| InvokeTool | calendar_tomorrow | 2 | (never reached a downstream) |  |
| InvokeTool | calendar_tomorrow | 3 | (never reached a downstream) |  |
| InvokeTool | calendar_tomorrow | 4 | `invoke_tool({"serverName":"adjutant","toolName":"adjutant__get_calendar_events","arguments":"{\u0022startTime\u0022:\u00222026-09-09T00:00:00Z\u0022,\u0…)` | error: Unknown tool 'adjutant__get_calendar_events' on server 'adjutant'. 'adjutant__get_calendar_events' is the typed tool name; call it directly … |
| InvokeTool | calendar_tomorrow | 5 | `invoke_tool({"serverName":"adjutant","toolName":"adjutant__get_calendar_events","arguments":"{\u0022startTime\u0022:\u00222026-09-09T00:00:00Z\u0022,\u0…)` | error: Unknown tool 'adjutant__get_calendar_events' on server 'adjutant'. 'adjutant__get_calendar_events' is the typed tool name; call it directly … |
| InvokeTool | calendar_tomorrow | 7 | (never reached a downstream) |  |
| InvokeTool | calendar_tomorrow | 8 | `invoke_tool({"serverName":"adjutant","toolName":"adjutant__get_calendar_events","arguments":"{\u0022startDate\u0022:\u00222026-09-09T00:00:00Z\u0022,\u0…)` | error: Unknown tool 'adjutant__get_calendar_events' on server 'adjutant'. 'adjutant__get_calendar_events' is the typed tool name; call it directly … |
| InvokeTool | calendar_tomorrow | 10 | (never reached a downstream) |  |
| InvokeTool | list_files_marimer | 2 | `invoke_tool({"serverName":"onedrive-marimer","toolName":"list_files","arguments":"/Documents/Reports"})` | error: 'arguments' must be the tool's argument object encoded as a JSON string, for example "{\"query\": \"text\"}". You sent: /Documents/Reports. … |
| InvokeTool | list_files_marimer | 3 | (never reached a downstream) |  |
| InvokeTool | list_files_marimer | 4 | (never reached a downstream) |  |
| InvokeTool | list_files_marimer | 5 | `invoke_tool({"serverName":"onedrive-marimer","toolName":"list_files","arguments":"/Documents/Reports"})` | error: 'arguments' must be the tool's argument object encoded as a JSON string, for example "{\"query\": \"text\"}". You sent: /Documents/Reports. … |
| InvokeTool | list_files_marimer | 6 | (never reached a downstream) |  |
| InvokeTool | list_files_marimer | 7 | (never reached a downstream) |  |
| InvokeTool | list_files_marimer | 8 | (never reached a downstream) |  |
| InvokeTool | list_files_marimer | 9 | `invoke_tool({"serverName":"onedrive-marimer","toolName":"list_files","arguments":null})` | error: An error occurred invoking 'list_files'. Argument mismatch for tool 'list_files' on 'onedrive-marimer'. Missing required parameter(s): [path… |
| InvokeTool | list_files_marimer | 10 | (never reached a downstream) |  |
| InvokeTool | list_files_personal | 1 | (never reached a downstream) |  |
| InvokeTool | list_files_personal | 2 | (never reached a downstream) |  |
| InvokeTool | list_files_personal | 3 | (never reached a downstream) | I'll need to connect to your OneDrive account to access the Photos folder. Do you want me to proceed… |
| InvokeTool | list_files_personal | 4 | (never reached a downstream) |  |
| InvokeTool | list_files_personal | 5 | (never reached a downstream) |  |
| InvokeTool | list_files_personal | 6 | (never reached a downstream) |  |
| InvokeTool | list_files_personal | 7 | (never reached a downstream) |  |
| InvokeTool | list_files_personal | 8 | (never reached a downstream) |  |
| InvokeTool | list_files_personal | 9 | (never reached a downstream) |  |
| InvokeTool | list_files_personal | 10 | (never reached a downstream) |  |
| InvokeTool | docs_search | 1 | (never reached a downstream) |  |
| InvokeTool | docs_search | 3 | `invoke_tool({"serverName":"microsoft-learn","toolName":"microsoft_docs_search","arguments":"How to configure the options pattern in ASP.NET Core"})` | error: 'arguments' must be the tool's argument object encoded as a JSON string, for example "{\"query\": \"text\"}". You sent: How to configure the… |
| InvokeTool | docs_search | 4 | `invoke_tool({"serverName":"microsoft-learn","toolName":"microsoft_docs_search","arguments":"how to configure the options pattern in ASP.NET Core"})` | error: 'arguments' must be the tool's argument object encoded as a JSON string, for example "{\"query\": \"text\"}". You sent: how to configure the… |
| InvokeTool | docs_search | 5 | (never reached a downstream) | I am unable to directly retrieve the documentation. However, I can provide a summary of how to confi… |
| InvokeTool | docs_search | 6 | `invoke_tool({"serverName":"microsoft-learn","toolName":"microsoft_learn__microsoft_docs_search","arguments":"{\u0022query\u0022: \u0022configure options…)` | error: Unknown tool 'microsoft_learn__microsoft_docs_search' on server 'microsoft-learn'. Available tools: [microsoft_code_sample_search, microsoft… |
| InvokeTool | docs_search | 7 | (never reached a downstream) | The Microsoft Learn Docs service does not currently have a skill document available that provides di… |
| InvokeTool | docs_search | 9 | `invoke_tool({"serverName":"microsoft-learn","toolName":"microsoft_docs_search","arguments":"configure options pattern in ASP.NET Core"})` | error: 'arguments' must be the tool's argument object encoded as a JSON string, for example "{\"query\": \"text\"}". You sent: configure options pa… |
| InvokeTool | docs_search | 10 | (never reached a downstream) |  |
