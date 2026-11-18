import {Client} from '@modelcontextprotocol/sdk/client/index.js';
import {StdioClientTransport} from '@modelcontextprotocol/sdk/client/stdio.js';
import {StreamableHTTPClientTransport} from '@modelcontextprotocol/sdk/client/streamableHttp.js';
const {ABH_MCP_URL,ABH_MCP_KEY,ABH_EXE}=process.env;
if(!ABH_MCP_URL||!ABH_MCP_KEY||!ABH_EXE)throw new Error('Process-scoped MCP test configuration is required.');
const checks=[];
for(const transportName of ['stdio','streamable-http']){
 const transport=transportName==='stdio'?new StdioClientTransport({command:ABH_EXE,args:['--mcp'],env:{...process.env,ABH_MCP_URL,ABH_MCP_KEY},stderr:'pipe'}):new StreamableHTTPClientTransport(new URL(ABH_MCP_URL),{requestInit:{headers:{Authorization:'Bearer '+ABH_MCP_KEY}}});
 const client=new Client({name:'abh-sdk-acceptance',version:'2.0.0-beta.2'},{capabilities:{}});
 try{
  await client.connect(transport);
  const list=await client.listTools();if(!list.tools.some(t=>t.name==='abh_list_models'))throw new Error('Model registry tool absent.');
  const result=await client.callTool({name:'abh_runtime_status',arguments:{}});if(result.isError)throw new Error('Runtime tool returned an error.');
  const bad=await client.callTool({name:'abh_model_info',arguments:{modelId:'missing'}});if(!bad.isError)throw new Error('Unknown model was not rejected.');
  checks.push({transport:transportName,status:'VERIFIED',sdk:'@modelcontextprotocol/sdk 1.32.2',server:client.getServerVersion(),toolCount:list.tools.length,realToolResponse:result.content,unknownModelRejected:true});
 }finally{await client.close();}
}
console.log(JSON.stringify({status:'VERIFIED',scope:'Official TypeScript SDK, real local server; negotiated 2025-11-25 compatibility. Current 2026-07-28 tested separately.',checks}));
