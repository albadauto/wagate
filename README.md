# WaGate — API de WhatsApp sobre o WAHA

Gateway de WhatsApp multi-instância no estilo **Z-API**, rodando na sua infraestrutura sobre o
[WAHA](https://waha.devlike.pro). Cada **instância** é um número conectado (uma sessão do WAHA); seus sistemas
enviam mensagens por uma API REST simples e recebem os eventos por **webhooks assinados com retentativa**.

- **API**: ASP.NET Core 10 (minimal APIs), documentada com **OpenAPI + Scalar** em `/docs`
- **Painel**: Blazor (Interactive Server) + **Tailwind CSS v4**
- **Banco**: SQL Server com EF Core 10 (migrations)
- **WhatsApp**: WAHA (engines GOWS, NOWEB ou WEBJS)

```
             ┌──────────────┐  JWT   ┌──────────────────────────────┐   X-Api-Key   ┌────────┐
 Navegador ─▶│ WaGate.Web   │──────▶ │ WaGate.Api                   │──────────────▶│  WAHA  │◀──▶ WhatsApp
             │ Blazor+TW    │        │  /api/*        (painel, JWT) │               │        │
             └──────────────┘        │  /instances/*  (envio)       │◀──────────────│        │
                                     │  /waha/webhook (HMAC-SHA512) │  eventos      └────────┘
 Seu sistema ── Client-Token ──────▶ │                              │
     ▲                               │  Outbox ─▶ Dispatcher ───────┼──▶ webhooks dos clientes
     └──── POST assinado (HMAC-SHA256) ◀────────────────────────────┘     (retry 8x, backoff)
                                                   │
                                              SQL Server
```

## Subindo tudo com Docker

```bash
cp .env.example .env      # troque as senhas e chaves
docker compose up -d --build
```

| Serviço | URL |
|---|---|
| Painel | http://localhost:5090 (login: `admin@wagate.local` / `Admin@123456`, definido no `.env`) |
| API + documentação Scalar | http://localhost:5080/docs |
| Dashboard do WAHA | http://localhost:3000/dashboard |

No painel: **Instâncias → Nova instância** → leia o QR Code em *WhatsApp → Aparelhos conectados*. Pronto.

## Desenvolvimento local

Pré-requisitos: .NET SDK 10, Docker (para SQL Server e WAHA) e, opcionalmente, Node 20+ para recompilar o Tailwind.

```bash
# 1. infraestrutura
docker compose -f docker-compose.dev.yml up -d

# 2. API  → http://localhost:5080/docs  (aplica as migrations e cria o admin no primeiro start)
dotnet run --project src/WaGate.Api

# 3. Painel → http://localhost:5090
dotnet run --project src/WaGate.Web

# Tailwind (opcional; o app.css compilado já está no repositório)
cd src/WaGate.Web && npm install && npm run css:watch
```

Com `node_modules` instalado, o build do `WaGate.Web` recompila o Tailwind automaticamente.

Em desenvolvimento o WAHA (no container) chama a API no host via `http://host.docker.internal:5080/waha/webhook`
(já configurado em `appsettings.json` e no `docker-compose.dev.yml`).

### Migrations

```bash
dotnet tool restore
dotnet ef migrations add NomeDaMigration -p src/WaGate.Infrastructure -s src/WaGate.Api -o Data/Migrations
dotnet ef database update -p src/WaGate.Infrastructure -s src/WaGate.Api   # opcional: a API já migra no start
```

### Testes

```bash
dotnet test
```

## Estrutura

```
src/
  WaGate.Domain/          Entidades (Account, Instance, Webhook, Message, WebhookDelivery, InboundEvent)
  WaGate.Shared/          DTOs/contratos compartilhados entre API e painel
  WaGate.Infrastructure/  EF Core + SQL Server, migrations, cliente tipado do WAHA, criptografia
  WaGate.Api/             Endpoints, autenticação, processamento de eventos, outbox e jobs em background
  WaGate.Web/             Painel Blazor + Tailwind
tests/WaGate.Tests/       Testes (assinaturas, normalização de eventos, cliente WAHA, retry)
```

## Autenticação

| Uso | Como |
|---|---|
| Painel / gestão (`/api/*`) | `POST /api/auth/login` → `Authorization: Bearer <jwt>` |
| Envio (`/instances/{id}/token/{token}/*`) | token da instância **na URL** + header **`Client-Token`** da conta |

O `Client-Token` fica em *Painel → Segurança* (pode ser ativado ou desativado por conta, como na Z-API); o ID, o token e a URL da instância ficam no card *Dados da instância*.
Ambos podem ser regenerados a qualquer momento.

## API de envio (estilo Z-API)

Base: `{API}/instances/{instanceId}/token/{token}`

| Método | Rota | Descrição |
|---|---|---|
| POST | `/send-text` | Texto (`phone`, `message`, `delayTyping`, `messageId` p/ responder, `mentions`) |
| POST | `/send-image` | Imagem por `url` ou `base64` (+ `caption`) |
| POST | `/send-document` | Documento/arquivo (`fileName`, `caption`) |
| POST | `/send-audio` | Nota de voz (converte para OGG/Opus) |
| POST | `/send-video` | Vídeo (`asNote` para vídeo redondo) |
| POST | `/send-location` | `latitude`, `longitude`, `title`, `address` |
| POST | `/send-contact` | vCard (`contactName`, `contactPhone`) |
| POST | `/send-reaction` | Reação a uma mensagem (`reaction: ""` remove) |
| POST | `/read-message` | Marca chat/mensagens como lidos |
| POST | `/typing` | Liga/desliga "digitando..." |
| GET | `/phone-exists/{phone}` | Verifica se o número tem WhatsApp |
| GET | `/status` | Status da conexão |
| GET | `/qr-code` · `/qr-code/image` | QR Code em base64 ou PNG |
| POST | `/restart` · `/disconnect` | Reinicia a sessão / desconecta o número |

`phone` aceita número com DDI (`5511999999999`, com ou sem máscara) ou id de grupo (`...@g.us`).

```bash
curl -X POST "http://localhost:5080/instances/$ID/token/$TOKEN/send-text" \
  -H "Content-Type: application/json" -H "Client-Token: $CLIENT_TOKEN" \
  -d '{ "phone": "5511999999999", "message": "Olá do WaGate!", "delayTyping": 2 }'
# → { "zaapId": "...", "messageId": "true_5511999999999@c.us_3EB0...", "id": "..." }
```

Erros seguem **ProblemDetails** (RFC 7807): `409` instância desconectada, `422` recusado pelo WAHA,
`502/503/504` WAHA com falha/indisponível, `429` limite de requisições por instância.

## Webhooks

Cadastre até 10 URLs por instância, escolhendo os eventos (ou `*` / curinga `message.*`):

| Evento | Quando |
|---|---|
| `message.received` | Mensagem recebida |
| `message.sent` | Mensagem enviada (pela API ou pelo celular) |
| `message.status` | Confirmação: `SENT`, `DELIVERED`, `READ`, `PLAYED`, `FAILED` |
| `message.reaction` · `message.revoked` | Reação / mensagem apagada |
| `instance.status` | Toda mudança de status da sessão |
| `instance.connected` · `instance.disconnected` | Conectou / caiu |
| `call.received` | Chamada recebida (rejeitada automaticamente se configurado) |
| `group.join` · `group.leave` · `presence.update` | Grupos e presença |
| `webhook.test` | Disparado pelo botão "Testar" |

Formato:

```json
{
  "id": "a3c1…",                       // = header X-WaGate-Delivery (use para idempotência)
  "event": "message.received",
  "instanceId": "3f2b…",
  "instanceName": "Atendimento",
  "connectedPhone": "5511987654321",
  "timestamp": 1792700000000,
  "data": {
    "messageId": "false_5511999990000@c.us_3EB0A1",
    "chatId": "5511999990000@c.us",
    "phone": "5511999990000",
    "fromMe": false,
    "isGroup": false,
    "senderName": "Carlos",
    "type": "text",                    // text, image, document, audio, video, location, contact, sticker…
    "text": "Oi, tem horário amanhã?",
    "media": null,                     // { url, mimetype, filename }
    "raw": { }                         // payload original do WAHA
  }
}
```

**Headers:** `X-WaGate-Event`, `X-WaGate-Delivery`, `X-WaGate-Attempt`, `X-WaGate-Timestamp`,
`X-WaGate-Signature: sha256=<hex>`.

**Assinatura:** `hex(HMAC-SHA256(secret, "{timestamp}.{corpo}"))`, com o `secret` do webhook.

```csharp
var expected = Convert.ToHexStringLower(
    HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{ts}.{body}")));
var ok = CryptographicOperations.FixedTimeEquals(
    Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(signature.Replace("sha256=", "")));
```

**Entrega:** responda `2xx` em até 15s. Falhas são repetidas em 10s, 30s, 2min, 10min, 30min, 1h, 3h e 6h
(±10% de jitter); após 8 tentativas, ou com `410 Gone`, a entrega é descartada. Tudo fica visível em
*Painel → Entregas de webhook*, com payload, resposta, tempo e botão de reenvio.

O outbox fica no SQL Server e cada entrega é "reivindicada" com `UPDATE` condicional, então é seguro rodar
**várias réplicas da API**.

## Como os eventos do WAHA entram

1. Ao criar/iniciar uma instância, a API cria a sessão no WAHA com `config.webhooks` apontando para
   `/waha/webhook`, com `hmac.key` e `metadata.instanceId`.
2. `/waha/webhook` valida `X-Webhook-Hmac` (HMAC-SHA512 do corpo cru), registra o evento bruto em
   `InboundEvents` (deduplicado pelo `id` do evento) e normaliza.
3. O status das sessões também é **reconciliado a cada 60s** consultando o WAHA, cobrindo webhooks perdidos.

## Configuração (`appsettings.json` / variáveis de ambiente)

| Chave | Padrão | Descrição |
|---|---|---|
| `ConnectionStrings:Default` | localhost | SQL Server |
| `Jwt:Key` | — | **Obrigatória**, ≥ 32 caracteres |
| `Waha:BaseUrl` / `Waha:ApiKey` | `http://localhost:3000` | Endereço e `WAHA_API_KEY` |
| `Waha:WebhookUrl` | `http://host.docker.internal:5080/waha/webhook` | Como o WAHA alcança a API |
| `Waha:WebhookHmacKey` | — | Chave HMAC das sessões (vazio desativa a validação) |
| `Waha:Events` | lista padrão | Eventos do WAHA assinados |
| `Security:EnforceClientToken` | `false` | Força `Client-Token` em todas as contas (senão vale o liga/desliga de cada conta em *Segurança*) |
| `Security:AllowRegistration` | `true` | Permite criar contas pelo painel |
| `RateLimit:InstancePerMinute` / `InstanceBurst` | 60 / 30 | Token bucket por instância |
| `Retention:InboundEventsDays` / `DeliveriesDays` | 7 / 30 | Limpeza automática |
| `Jobs:StatusSyncSeconds` | 60 | Intervalo da reconciliação de status |
| `Seed:AdminEmail` / `Seed:AdminPassword` | — | Conta criada no primeiro start |
| `Database:MigrateOnStartup` | `true` | Aplica migrations ao iniciar |

No painel (`WaGate.Web`): `Api:BaseUrl` (URL interna da API), `Api:PublicUrl` (URL exibida nos exemplos e no
link da documentação) e `DataProtection:KeysPath` (persistência das chaves que cifram a sessão do navegador).

## Planos e cobrança (Stripe)

| Plano | Preço | Instâncias | Teste |
|---|---|---|---|
| **Ultimate** | R$ 99,00 /mês por instância | 1 a 9 | **7 dias grátis** (uma vez por conta / CPF-CNPJ) |
| **Partner** | R$ 89,90 /mês por instância | 10 a 2.000 | — |

Valores, limites e período de teste ficam em `Billing:Ultimate` / `Billing:Partner` (valores em centavos).
Os preços são criados **automaticamente** no Stripe na primeira assinatura (por `lookup_key`, ex.:
`wagate_ultimate_9900_brl_month`). Se preferir usar preços já cadastrados, informe `Billing:Ultimate:PriceId`.

**Configuração**

1. Em [dashboard.stripe.com](https://dashboard.stripe.com) (modo teste), copie a *Secret key* para `Stripe:SecretKey`
   (`STRIPE_SECRET_KEY` no `.env`).
2. Crie o webhook apontando para `https://SUA-API/api/billing/stripe/webhook` com os eventos
   `checkout.session.completed`, `customer.subscription.created`, `customer.subscription.updated`,
   `customer.subscription.deleted`, `invoice.paid` e `invoice.payment_failed`; copie o *Signing secret* para
   `Stripe:WebhookSecret`. Em desenvolvimento:
   ```bash
   stripe listen --forward-to localhost:5080/api/billing/stripe/webhook   # mostra o whsec_ para usar
   ```
3. Ative o **Portal do cliente** em *Settings → Billing → Customer portal* (troca de cartão, faturas, cancelamento).
4. `Billing:PublicAppUrl` = URL do painel (retorno do Checkout).

**Regras**

- Sem `Stripe:SecretKey` a cobrança fica **desligada** (útil em desenvolvimento): o limite é `Account.MaxInstances`.
- Com a cobrança ligada: criar instância exige assinatura `trialing`, `active` ou `past_due`, e o limite é a quantidade
  contratada. Com a assinatura cancelada/não paga, a API de envio responde **402 Payment Required**.
- O teste grátis pede o cartão no início e cancela sozinho se não houver meio de pagamento ao fim dos 7 dias.
- Troca de plano e de quantidade com cobrança proporcional (`POST /api/billing/change`). Migrar do Ultimate em teste para o
  Partner encerra o teste e começa a cobrança.
- Webhooks do Stripe são validados (`Stripe-Signature`, tolerância de 5 min) e deduplicados pela tabela `StripeEvents`.

## Cadastro

Nome e sobrenome, e-mail, WhatsApp (com DDD), **CPF ou CNPJ** (dígitos verificadores conferidos; um cadastro por
documento), senha forte (8+ caracteres, maiúscula, minúscula e número; senhas óbvias recusadas) com confirmação,
aceite dos termos (`/terms` — **substitua o texto-modelo**) e campo-armadilha anti-bot. O login bloqueia a conta por
15 minutos após 5 senhas erradas, e o cadastro/login têm limite de 10 requisições por minuto por IP.

## WAHA não funciona? Diagnóstico

O painel mostra o status em *Visão geral* (e um alerta em *Instâncias* quando há problema), via `GET /api/system/waha`.

| Sintoma | Causa provável |
|---|---|
| "O WAHA recusou a API key" | `Waha:ApiKey` ≠ `WAHA_API_KEY`. **Valores comuns (`admin`, `123`, `waha`…) são trocados pelo WAHA por uma chave aleatória** — veja `docker compose logs waha`. |
| "Não foi possível conectar" | Container parado ou `Waha:BaseUrl` errado (no compose é `http://waha:3000`; rodando a API fora do Docker, `http://localhost:3000`). |
| QR aparece mas o status nunca muda / mensagens não chegam | O WAHA não alcança a API: confira `Waha:WebhookUrl` ("Último evento recebido do WAHA" fica vazio). Rodando a API no Windows fora do Docker use `http://host.docker.internal:5080/waha/webhook` (a API escuta em `http://*:5080`). |
| Webhooks do WAHA recusados (401 no log) | `Waha:WebhookHmacKey` foi trocada: a API reconfigura as sessões no próximo start. |
| Login do dashboard do WAHA não funciona | Senha "comum" (ex.: `admin`) substituída por uma aleatória; use outra senha. |

Correções feitas nesta versão: o `PUT /api/sessions/{nome}` do WAHA **reinicia a sessão**; antes ele era chamado a cada
"Iniciar"/"Ler QR Code" (derrubando sessões conectadas e gerando QR novo a cada abertura). Agora só é chamado quando a
configuração do webhook mudou. Sessões `FAILED` são reiniciadas, e envios (POST) não são mais repetidos automaticamente
em timeout, evitando mensagens duplicadas.

## Produção — checklist

- Troque **todas** as chaves do `.env` (JWT, WAHA API key, HMAC, senha do SQL e do admin).
- Coloque API e painel atrás de um proxy com HTTPS (Nginx, Traefik, Caddy) e defina `PUBLIC_API_URL`.
- Não exponha a porta do WAHA (3000) publicamente; só a API precisa falar com ele.
- Considere `Security:AllowRegistration=false` depois de criar as contas.
- Use chaves `sk_live_`/`whsec_` de produção do Stripe e revise os Termos/Política em `/terms`.
- Faça backup dos volumes `sqldata` e `waha_sessions` (é onde ficam os logins do WhatsApp).
- Use números que respeitem as políticas do WhatsApp: envio em massa não solicitado leva a banimento.
