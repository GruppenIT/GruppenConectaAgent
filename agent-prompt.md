# Sessão 2 — Agente Windows para Gruppen Remote Support

## Contexto

Você vai desenvolver o **Agente Windows** para a plataforma Gruppen Remote Support.
Este agente roda como serviço no Windows, conecta-se à Console Central via WebSocket
(protocolo binário), captura a tela, e encaminha frames JPEG para a console. Ele também
recebe comandos de mouse e teclado e os executa localmente.

A Console Central já está implementada e funcionando. Abaixo estão todas as especificações
que o agente precisa seguir para ser compatível.

---

## Stack do Agente

- **Linguagem:** C# / .NET 8
- **Compilação:** Self-contained, win-x64
- **Instalador:** MSI via WiX v5
- **Execução:** Windows Service (pode ter modo console para debug)

---

## Endpoint de Conexão

O agente se conecta na Console via WebSocket:

```
wss://<console-host>:<port>/ws/agent
```

Em desenvolvimento (sem TLS):
```
ws://localhost:3001/ws/agent
```

---

## Protocolo Binário

### Formato do Wire

Cada mensagem segue este formato:

```
[1 byte: tipo] [4 bytes: tamanho do payload (Big Endian)] [N bytes: payload]
```

- O campo **tamanho** é o número de bytes do payload, **não inclui** o header de 5 bytes.
- Payloads JSON são codificados em UTF-8.
- O campo tipo identifica o tipo da mensagem (ver tabela abaixo).

### Tipos de Mensagem

| Código | Nome | Direção | Payload |
|--------|------|---------|---------|
| `0x01` | AUTH | Agente → Console | JSON: `{ "agent_id": "string", "token": "string", "hostname": "string", "os_info": "string" }` |
| `0x02` | AUTH_OK | Console → Agente | JSON: `{ "agent_id": "string" }` |
| `0x03` | START_STREAM | Console → Agente | JSON: `{ "quality": number (1-100), "fps_max": number }` |
| `0x04` | FRAME | Agente → Console | Binário: `[4B seq BigEndian][4B timestamp_ms BigEndian][JPEG bytes]` |
| `0x05` | MOUSE_EVENT | Console → Agente | JSON: `{ "x": number, "y": number, "button": number, "action": "move"\|"down"\|"up"\|"click"\|"dblclick" }` |
| `0x06` | KEY_EVENT | Console → Agente | JSON: `{ "key": "string", "action": "down"\|"up", "modifiers": ["ctrl","alt","shift","meta"] }` |
| `0x07` | STOP_STREAM | Console → Agente | Vazio (0 bytes de payload) |
| `0x08` | HEARTBEAT | Agente → Console | JSON: `{ "uptime": number, "cpu": number, "mem": number }` |
| `0x09` | HEARTBEAT_ACK | Console → Agente | Vazio (0 bytes de payload) |
| `0xFF` | ERROR | Ambas direções | JSON: `{ "code": "string", "message": "string" }` |

---

## Fluxo de Autenticação

1. Agente conecta via WebSocket em `/ws/agent`
2. Agente envia mensagem `AUTH` (0x01) com:
   ```json
   {
     "agent_id": "agent-demo-001",
     "token": "demo-token-12345",
     "hostname": "DESKTOP-ABC123",
     "os_info": "Windows 11 Pro 23H2"
   }
   ```
3. Console valida:
   - Busca o agente no banco pelo `agent_id`
   - Compara `token` com `token_hash` via bcrypt
4. Se válido: Console responde `AUTH_OK` (0x02):
   ```json
   { "agent_id": "agent-demo-001" }
   ```
5. Se inválido: Console responde `ERROR` (0xFF) e fecha a conexão:
   ```json
   { "code": "INVALID_TOKEN", "message": "Invalid token" }
   ```

**Timeout:** O agente tem 10 segundos para enviar AUTH após conectar, ou a conexão é fechada.

---

## Fluxo de Captura de Tela

1. Após autenticação, o agente aguarda o comando `START_STREAM` (0x03)
2. Ao receber START_STREAM com `{ quality: 70, fps_max: 15 }`:
   - Inicia captura da tela principal
   - Cada frame é codificado como JPEG com a qualidade especificada
   - Envia frames como mensagem `FRAME` (0x04)
   - Respeita fps_max (não enviar mais de N frames por segundo)
3. Ao receber `STOP_STREAM` (0x07): para a captura

### Formato do Frame (0x04)

O payload de um FRAME é binário (não é JSON):

```
[4 bytes: sequence number, Big Endian, uint32]
[4 bytes: timestamp em ms desde início da captura, Big Endian, uint32]
[N bytes: JPEG data]
```

- `seq` começa em 1 e incrementa a cada frame
- `timestamp_ms` é relativo ao início da captura (primeiro frame = 0)

---

## Heartbeat

- O agente deve enviar `HEARTBEAT` (0x08) a cada **30 segundos**
- Payload:
  ```json
  {
    "uptime": 3600,
    "cpu": 45.2,
    "mem": 67.8
  }
  ```
  - `uptime`: segundos desde que o agente iniciou
  - `cpu`: % de uso de CPU do sistema
  - `mem`: % de uso de memória do sistema
- A console responde com `HEARTBEAT_ACK` (0x09) (payload vazio)
- Se a console não receber heartbeat por **90 segundos**, desconecta o agente

---

## Recebendo Comandos de Input

### Mouse (0x05)

```json
{
  "x": 500,
  "y": 300,
  "button": 0,
  "action": "click"
}
```

- `x`, `y`: coordenadas absolutas na tela (pixels)
- `button`: 0 = esquerdo, 1 = meio, 2 = direito
- `action`: `"move"`, `"down"`, `"up"`, `"click"`, `"dblclick"`

O agente deve:
- `move`: Mover o cursor para (x, y)
- `down`: Mover + pressionar botão
- `up`: Mover + soltar botão
- `click`: Mover + click completo
- `dblclick`: Mover + duplo click

### Teclado (0x06)

```json
{
  "key": "a",
  "action": "down",
  "modifiers": ["ctrl"]
}
```

- `key`: Nome da tecla (usa nomenclatura do JavaScript `KeyboardEvent.key`)
- `action`: `"down"` ou `"up"`
- `modifiers`: Array de modificadores ativos (`"ctrl"`, `"alt"`, `"shift"`, `"meta"`)

O agente deve:
- Pressionar/soltar modificadores conforme o array
- Executar a tecla especificada

---

## Como Registrar um Agente na Console

Para registrar um novo agente e obter credenciais:

### Via API REST (requer token JWT de admin):

```bash
# 1. Login como admin
curl -X POST http://localhost:3001/api/auth/login \
  -H "Content-Type: application/json" \
  -d '{"username":"admin","password":"admin123"}' \
  -c cookies.txt

# 2. Registrar agente
curl -X POST http://localhost:3001/api/agents \
  -H "Content-Type: application/json" \
  -b cookies.txt \
  -d '{"name":"PC Escritório 01"}'
```

Resposta:
```json
{
  "agent": { "id": "agent-a1b2c3d4", "name": "PC Escritório 01" },
  "token": "550e8400-e29b-41d4-a716-446655440000",
  "warning": "Save this token now. It cannot be retrieved again."
}
```

### Via seed (desenvolvimento):

O seed padrão cria um agente demo:
- **Agent ID:** `agent-demo-001`
- **Token:** `demo-token-12345`

---

## Exemplos de Mensagens Binárias

### AUTH (Agente → Console)

```
Hex: 01 00 00 00 5B 7B 22 61 67 65 6E 74 ...
     ^  ^---------^ ^------------------------
     |  payload_size  JSON payload (UTF-8)
     tipo (0x01)
```

Exemplo completo em C#:
```csharp
// Encode
var payload = JsonSerializer.SerializeToUtf8Bytes(new {
    agent_id = "agent-demo-001",
    token = "demo-token-12345",
    hostname = Environment.MachineName,
    os_info = $"{Environment.OSVersion}"
});

var msg = new byte[5 + payload.Length];
msg[0] = 0x01; // AUTH
BinaryPrimitives.WriteUInt32BigEndian(msg.AsSpan(1), (uint)payload.Length);
payload.CopyTo(msg, 5);

await ws.SendAsync(msg, WebSocketMessageType.Binary, true, ct);
```

### FRAME (Agente → Console)

```
Hex: 04 00 01 00 08 00 00 00 01 00 00 07 D0 FF D8 FF E0 ...
     ^  ^---------^ ^---------^ ^---------^ ^-----------
     |  payload_size  seq=1      ts=2000ms   JPEG data
     tipo (0x04)
```

Exemplo em C#:
```csharp
byte[] jpeg = CaptureScreenAsJpeg(quality);
uint seq = _frameSeq++;
uint tsMs = (uint)(DateTimeOffset.UtcNow - _captureStart).TotalMilliseconds;

var meta = new byte[8];
BinaryPrimitives.WriteUInt32BigEndian(meta.AsSpan(0), seq);
BinaryPrimitives.WriteUInt32BigEndian(meta.AsSpan(4), tsMs);

var payload = new byte[8 + jpeg.Length];
meta.CopyTo(payload, 0);
jpeg.CopyTo(payload, 8);

var msg = new byte[5 + payload.Length];
msg[0] = 0x04; // FRAME
BinaryPrimitives.WriteUInt32BigEndian(msg.AsSpan(1), (uint)payload.Length);
payload.CopyTo(msg, 5);

await ws.SendAsync(msg, WebSocketMessageType.Binary, true, ct);
```

### Decodificando uma mensagem recebida (C#)

```csharp
// buffer contém a mensagem completa recebida via WebSocket
byte type = buffer[0];
uint payloadSize = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(1));
var payload = buffer.AsSpan(5, (int)payloadSize);

switch (type)
{
    case 0x02: // AUTH_OK
        var authOk = JsonSerializer.Deserialize<AuthOkResponse>(payload);
        break;
    case 0x03: // START_STREAM
        var config = JsonSerializer.Deserialize<StreamConfig>(payload);
        StartCapture(config.Quality, config.FpsMax);
        break;
    case 0x05: // MOUSE_EVENT
        var mouse = JsonSerializer.Deserialize<MouseEvent>(payload);
        SimulateMouse(mouse);
        break;
    case 0x06: // KEY_EVENT
        var key = JsonSerializer.Deserialize<KeyEvent>(payload);
        SimulateKey(key);
        break;
    case 0x07: // STOP_STREAM
        StopCapture();
        break;
    case 0x09: // HEARTBEAT_ACK
        // noop
        break;
    case 0xFF: // ERROR
        var error = JsonSerializer.Deserialize<ErrorResponse>(payload);
        Log.Error($"Server error: {error.Code} - {error.Message}");
        break;
}
```

---

## Requisitos de Implementação

### Captura de tela
- Usar `Desktop Duplication API` (DXGI) para performance
- Fallback para `Graphics.CopyFromScreen` se DXGI não estiver disponível
- Encoder JPEG: usar `System.Drawing` ou `SkiaSharp`
- Respeitar fps_max e quality recebidos no START_STREAM

### Simulação de input
- Usar `SendInput` do Win32 para mouse e teclado
- Mapear `KeyboardEvent.key` do JavaScript para Virtual Key codes do Windows
- Suportar modificadores (Ctrl, Alt, Shift, Meta/Win)

### Resiliência
- Reconectar automaticamente se a conexão cair (backoff exponencial)
- Manter estado: se estava capturando antes da queda, retomar após reconexão
- Buffer de envio para não sobrecarregar a rede

### Configuração
- Arquivo `config.json` ou argumentos de linha de comando:
  - `console_url`: URL da console (ex: `wss://console.gruppen.com.br/ws/agent`)
  - `agent_id`: ID do agente
  - `agent_token`: Token de autenticação
- Configuração pode vir do registro do Windows (HKLM) quando instalado via MSI

### Logging
- Log em arquivo rotativo
- Níveis: Debug, Info, Warning, Error
- Path: `C:\ProgramData\Gruppen\RemoteAgent\logs\`

---

## Compilação

```bash
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

Resultado: um único `.exe` sem dependência de .NET instalado.

---

## Geração do MSI com WiX v5

### Estrutura do projeto WiX

```xml
<!-- Package.wxs -->
<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs">
  <Package Name="Gruppen Remote Agent"
           Manufacturer="Gruppen IT"
           Version="1.0.0"
           UpgradeCode="PUT-GUID-HERE">

    <MajorUpgrade DowngradeErrorMessage="Uma versão mais recente já está instalada." />

    <Feature Id="Main">
      <ComponentGroupRef Id="ProductComponents" />
      <ComponentRef Id="ServiceComponent" />
    </Feature>
  </Package>

  <Fragment>
    <StandardDirectory Id="ProgramFiles64Folder">
      <Directory Id="INSTALLFOLDER" Name="Gruppen Remote Agent">
        <Component Id="ServiceComponent">
          <File Source="$(var.PublishDir)\GruppenRemoteAgent.exe" />
          <ServiceInstall
            Id="ServiceInstaller"
            Name="GruppenRemoteAgent"
            DisplayName="Gruppen Remote Agent"
            Description="Agente de suporte remoto Gruppen"
            Start="auto"
            Type="ownProcess"
            ErrorControl="normal"
            Account="LocalSystem" />
          <ServiceControl
            Id="ServiceControl"
            Name="GruppenRemoteAgent"
            Start="install"
            Stop="both"
            Remove="uninstall"
            Wait="yes" />
        </Component>
      </Directory>
    </StandardDirectory>
  </Fragment>

  <Fragment>
    <ComponentGroup Id="ProductComponents" Directory="INSTALLFOLDER">
      <Component>
        <File Source="$(var.PublishDir)\config.json" />
      </Component>
    </ComponentGroup>
  </Fragment>
</Wix>
```

### Build do MSI

```bash
# Instalar WiX v5
dotnet tool install --global wix

# Build
wix build Package.wxs -o GruppenRemoteAgent.msi -d PublishDir=bin\Release\net8.0\win-x64\publish
```

---

## Estrutura sugerida do projeto C#

```
GruppenRemoteAgent/
├── GruppenRemoteAgent.sln
├── src/
│   ├── Program.cs              # Entry point, host builder
│   ├── AgentService.cs         # BackgroundService principal
│   ├── Config/
│   │   └── AgentConfig.cs      # Modelo de configuração
│   ├── Protocol/
│   │   ├── MessageTypes.cs     # Constantes dos tipos
│   │   ├── BinaryProtocol.cs   # Encode/decode
│   │   └── Models.cs           # DTOs (AuthPayload, MouseEvent, etc.)
│   ├── Network/
│   │   └── WebSocketClient.cs  # Conexão + reconnect
│   ├── Capture/
│   │   ├── IScreenCapture.cs   # Interface
│   │   ├── DxgiCapture.cs      # DXGI Desktop Duplication
│   │   └── GdiCapture.cs       # Fallback GDI+
│   ├── Input/
│   │   ├── MouseSimulator.cs   # SendInput para mouse
│   │   └── KeyboardSimulator.cs # SendInput para teclado
│   └── Diagnostics/
│       └── SystemInfo.cs       # CPU, memória, uptime
├── installer/
│   └── Package.wxs             # WiX manifest
├── config.json                 # Configuração padrão
└── GruppenRemoteAgent.csproj
```

---

## Testes sugeridos

1. **Autenticação:** Conectar → AUTH → receber AUTH_OK
2. **Auth inválida:** Token errado → receber ERROR + desconexão
3. **Heartbeat:** Enviar a cada 30s → receber HEARTBEAT_ACK
4. **Captura:** Receber START_STREAM → enviar FRAMEs → receber STOP_STREAM → parar
5. **Mouse:** Receber MOUSE_EVENT → cursor se move corretamente
6. **Teclado:** Receber KEY_EVENT → tecla pressionada corretamente
7. **Reconexão:** Desconectar → agente reconecta automaticamente
8. **Instalação:** MSI instala o serviço e inicia automaticamente
