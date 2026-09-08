### Run: `qwen/qwen3-8b` via openai (https://openrouter.ai/api/v1), thinking off, 10 runs/task, 2026-09-08 20:13 UTC
Model info: `{"id":"qwen/qwen3-8b","canonical_slug":"qwen/qwen3-8b-04-28","hugging_face_id":"Qwen/Qwen3-8B","name":"Qwen: Qwen3 8B","created":1745876632,"description":"Qwen3-8B is a dense 8.2B parameter causal language model from the Qwen3 series, designed for both reasoning-heavy tasks and efficient dialogue. It supports seamless switching between \"thinking\" mode for math,...","context_length":131072,"architecture":{"modality":"text->text","input_modalities":["text"],"output_modalities":["text"],"tokenizer":"Qwen3","instruct_type":"qwen3"},"pricing":{"prompt":"0.000000117","completion":"0.000000455"},"top_provider":{"context_length":131072,"max_completion_tokens":8192,"is_moderated":false},"per_request_limits":null,"supported_parameters":["frequency_penalty","include_reasoning","max_tokens","presence_penalty","reasoning","response_format","seed","stop","temperature","tool_choice","tools","top_k","top_p"],"default_parameters":{"temperature":0.6,"top_p":0.95,"top_k":20,"frequency_penalty":null,"presence_penalty":null,"repetition_penalty":null},"supported_voices":null,"knowledge_cutoff":"2025-03-31","expiration_date":null,"links":{"details":"/api/v1/models/qwen/qwen3-8b-04-28/endpoints"},"benchmarks":{"design_arena":[],"artificial_analysis":{"intelligence_index":5.2,"coding_index":9,"agentic_index":0.8}},"reasoning":{"mandatory":false,"default_enabled":true}}`

#### Reliability by condition and task

| Condition | Task | Runs | First-call success | Completed | Wrong tool first | Avg steps to done | Avg turns | Avg input tok | Avg output tok | Errors |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| Eager | calendar_tomorrow | 10 | 10/10 (100%) | 10/10 (100%) | 0 | 1.0 | 1.0 | 3991 | 68 | 0 |
| Eager | docs_search | 10 | 10/10 (100%) | 10/10 (100%) | 0 | 1.0 | 1.0 | 4000 | 35 | 0 |
| Eager | list_files_marimer | 10 | 10/10 (100%) | 10/10 (100%) | 0 | 1.0 | 1.0 | 4002 | 26 | 0 |
| Eager | list_files_personal | 10 | 10/10 (100%) | 10/10 (100%) | 0 | 1.0 | 1.0 | 3998 | 29 | 0 |
| Eager | send_email | 10 | 10/10 (100%) | 10/10 (100%) | 0 | 1.0 | 1.0 | 4013 | 48 | 0 |
| InvokeTool | calendar_tomorrow | 10 | 0/10 (0%) | 0/10 (0%) | 0 | - | 1.0 | 2233 | 21 | 0 |
| InvokeTool | docs_search | 10 | 0/10 (0%) | 0/10 (0%) | 0 | - | 1.0 | 2242 | 53 | 10 |
| InvokeTool | list_files_marimer | 10 | 0/10 (0%) | 0/10 (0%) | 10 | - | 5.0 | 12066 | 275 | 0 |
| InvokeTool | list_files_personal | 10 | 10/10 (100%) | 10/10 (100%) | 0 | 3.0 | 3.0 | 9361 | 81 | 0 |
| InvokeTool | send_email | 10 | 0/10 (0%) | 0/10 (0%) | 10 | - | 2.0 | 4599 | 88 | 0 |
| Lazy | calendar_tomorrow | 10 | 10/10 (100%) | 10/10 (100%) | 0 | 2.0 | 2.0 | 6687 | 67 | 0 |
| Lazy | docs_search | 10 | 10/10 (100%) | 10/10 (100%) | 0 | 2.0 | 2.0 | 6890 | 64 | 0 |
| Lazy | list_files_marimer | 10 | 10/10 (100%) | 10/10 (100%) | 0 | 2.0 | 2.0 | 6965 | 55 | 0 |
| Lazy | list_files_personal | 10 | 10/10 (100%) | 10/10 (100%) | 0 | 2.0 | 2.0 | 6735 | 51 | 0 |
| Lazy | send_email | 10 | 10/10 (100%) | 10/10 (100%) | 0 | 2.0 | 2.0 | 6730 | 68 | 0 |

#### Reliability by condition (all tasks)

| Condition | Runs | First-call success | Completed | Avg input tok / run | Avg output tok / run | Avg ms / run |
|---|---:|---:|---:|---:|---:|---:|
| Eager | 50 | 50/50 (100%) | 50/50 (100%) | 4001 | 41 | 1503 |
| InvokeTool | 50 | 10/50 (20%) | 10/50 (20%) | 6100 | 104 | 3554 |
| Lazy | 50 | 50/50 (100%) | 50/50 (100%) | 6801 | 61 | 2880 |

#### tools/list size by condition

| Condition | Tools at start | Bytes at start | Tools at end | Bytes at end |
|---|---:|---:|---:|---:|
| Eager | 27 | 13583 | 27 | 13583 |
| Lazy | 14 | 7744 | 22 | 11122 |
| InvokeTool | 13 | 6994 | 13 | 6994 |

#### First-attempt failures (what the model actually sent)

| Condition | Task | Run | First downstream call | Result |
|---|---|---:|---|---|
| InvokeTool | send_email | 1 | `invoke_tool({"serverName":"email-service","toolName":"send_email","arguments":"{\u0022to\u0022: \u0022alice@example.com\u0022, \u0022subject\u0022: \u00…)` | error: Server 'email-service' not found. |
| InvokeTool | send_email | 2 | `invoke_tool({"serverName":"email-service","toolName":"send_email","arguments":"{\u0022to\u0022: \u0022alice@example.com\u0022, \u0022subject\u0022: \u00…)` | error: Server 'email-service' not found. |
| InvokeTool | send_email | 3 | `invoke_tool({"serverName":"email-service","toolName":"send_email","arguments":"{\u0022to\u0022: \u0022alice@example.com\u0022, \u0022subject\u0022: \u00…)` | error: Server 'email-service' not found. |
| InvokeTool | send_email | 4 | `invoke_tool({"serverName":"email-service","toolName":"send_email","arguments":"{\u0022to\u0022: \u0022alice@example.com\u0022, \u0022subject\u0022: \u00…)` | error: Server 'email-service' not found. |
| InvokeTool | send_email | 5 | `invoke_tool({"serverName":"email-service","toolName":"send_email","arguments":"{\u0022to\u0022: \u0022alice@example.com\u0022, \u0022subject\u0022: \u00…)` | error: Server 'email-service' not found. |
| InvokeTool | send_email | 6 | `invoke_tool({"serverName":"email-service","toolName":"send_email","arguments":"{\u0022to\u0022: \u0022alice@example.com\u0022, \u0022subject\u0022: \u00…)` | error: Server 'email-service' not found. |
| InvokeTool | send_email | 7 | `invoke_tool({"serverName":"email-service","toolName":"send_email","arguments":"{\u0022to\u0022: \u0022alice@example.com\u0022, \u0022subject\u0022: \u00…)` | error: Server 'email-service' not found. |
| InvokeTool | send_email | 8 | `invoke_tool({"serverName":"email-service","toolName":"send_email","arguments":"{\u0022to\u0022: \u0022alice@example.com\u0022, \u0022subject\u0022: \u00…)` | error: Server 'email-service' not found. |
| InvokeTool | send_email | 9 | `invoke_tool({"serverName":"email-service","toolName":"send_email","arguments":"{\u0022to\u0022: \u0022alice@example.com\u0022, \u0022subject\u0022: \u00…)` | error: Server 'email-service' not found. |
| InvokeTool | send_email | 10 | `invoke_tool({"serverName":"email-service","toolName":"send_email","arguments":"{\u0022to\u0022: \u0022alice@example.com\u0022, \u0022subject\u0022: \u00…)` | error: Server 'email-service' not found. |
| InvokeTool | calendar_tomorrow | 1 | (never reached a downstream) | I don't have access to your personal calendar. Please check your calendar application or device for … |
| InvokeTool | calendar_tomorrow | 2 | (never reached a downstream) | I don't have access to your personal calendar. Please check your calendar application or device for … |
| InvokeTool | calendar_tomorrow | 3 | (never reached a downstream) | I don't have access to your personal calendar. Please check your calendar application or device for … |
| InvokeTool | calendar_tomorrow | 4 | (never reached a downstream) | I don't have access to your personal calendar. Please check your calendar application or device for … |
| InvokeTool | calendar_tomorrow | 5 | (never reached a downstream) | I don't have access to your personal calendar. Please check your calendar application or device for … |
| InvokeTool | calendar_tomorrow | 6 | (never reached a downstream) | I don't have access to your personal calendar. Please check your calendar application or device for … |
| InvokeTool | calendar_tomorrow | 7 | (never reached a downstream) | I don't have access to your personal calendar. Please check your calendar application or device for … |
| InvokeTool | calendar_tomorrow | 8 | (never reached a downstream) | I don't have access to your personal calendar. Please check your calendar application or device for … |
| InvokeTool | calendar_tomorrow | 9 | (never reached a downstream) | I don't have access to your personal calendar. Please check your calendar application or device for … |
| InvokeTool | calendar_tomorrow | 10 | (never reached a downstream) | I don't have access to your personal calendar. Please check your calendar application or device for … |
| InvokeTool | list_files_marimer | 1 | `invoke_tool({"serverName":"mcp-aggregator","toolName":"list_files","arguments":"{\u0022folder_path\u0022: \u0022/Documents/Reports\u0022, \u0022company\…)` | error: Server 'mcp-aggregator' not found. |
| InvokeTool | list_files_marimer | 2 | `invoke_tool({"serverName":"mcp-aggregator","toolName":"list_files","arguments":"{\u0022folder_path\u0022: \u0022/Documents/Reports\u0022, \u0022company\…)` | error: Server 'mcp-aggregator' not found. |
| InvokeTool | list_files_marimer | 3 | `invoke_tool({"serverName":"mcp-aggregator","toolName":"list_files","arguments":"{\u0022folder_path\u0022: \u0022/Documents/Reports\u0022, \u0022company\…)` | error: Server 'mcp-aggregator' not found. |
| InvokeTool | list_files_marimer | 4 | `invoke_tool({"serverName":"mcp-aggregator","toolName":"list_files","arguments":"{\u0022folder_path\u0022: \u0022/Documents/Reports\u0022, \u0022company\…)` | error: Server 'mcp-aggregator' not found. |
| InvokeTool | list_files_marimer | 5 | `invoke_tool({"serverName":"mcp-aggregator","toolName":"list_files","arguments":"{\u0022folder_path\u0022: \u0022/Documents/Reports\u0022, \u0022company\…)` | error: Server 'mcp-aggregator' not found. |
| InvokeTool | list_files_marimer | 6 | `invoke_tool({"serverName":"mcp-aggregator","toolName":"list_files","arguments":"{\u0022folder_path\u0022: \u0022/Documents/Reports\u0022, \u0022company\…)` | error: Server 'mcp-aggregator' not found. |
| InvokeTool | list_files_marimer | 7 | `invoke_tool({"serverName":"mcp-aggregator","toolName":"list_files","arguments":"{\u0022folder_path\u0022: \u0022/Documents/Reports\u0022, \u0022company\…)` | error: Server 'mcp-aggregator' not found. |
| InvokeTool | list_files_marimer | 8 | `invoke_tool({"serverName":"mcp-aggregator","toolName":"list_files","arguments":"{\u0022folder_path\u0022: \u0022/Documents/Reports\u0022, \u0022company\…)` | error: Server 'mcp-aggregator' not found. |
| InvokeTool | list_files_marimer | 9 | `invoke_tool({"serverName":"mcp-aggregator","toolName":"list_files","arguments":"{\u0022folder_path\u0022: \u0022/Documents/Reports\u0022, \u0022company\…)` | error: Server 'mcp-aggregator' not found. |
| InvokeTool | list_files_marimer | 10 | `invoke_tool({"serverName":"mcp-aggregator","toolName":"list_files","arguments":"{\u0022folder_path\u0022: \u0022/Documents/Reports\u0022, \u0022company\…)` | error: Server 'mcp-aggregator' not found. |
| InvokeTool | docs_search | 1 | (never reached a downstream) | ClientResultException: HTTP 400 (: )

Provider returned error |
| InvokeTool | docs_search | 2 | (never reached a downstream) | ClientResultException: HTTP 400 (: )

Provider returned error |
| InvokeTool | docs_search | 3 | (never reached a downstream) | ClientResultException: HTTP 400 (: )

Provider returned error |
| InvokeTool | docs_search | 4 | (never reached a downstream) | ClientResultException: HTTP 400 (: )

Provider returned error |
| InvokeTool | docs_search | 5 | (never reached a downstream) | ClientResultException: HTTP 400 (: )

Provider returned error |
| InvokeTool | docs_search | 6 | (never reached a downstream) | ClientResultException: HTTP 400 (: )

Provider returned error |
| InvokeTool | docs_search | 7 | (never reached a downstream) | ClientResultException: HTTP 400 (: )

Provider returned error |
| InvokeTool | docs_search | 8 | (never reached a downstream) | ClientResultException: HTTP 400 (: )

Provider returned error |
| InvokeTool | docs_search | 9 | (never reached a downstream) | ClientResultException: HTTP 400 (: )

Provider returned error |
| InvokeTool | docs_search | 10 | (never reached a downstream) | ClientResultException: HTTP 400 (: )

Provider returned error |
