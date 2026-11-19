# GilRouting

GilRouting is a Final Fantasy XIV Dalamud plugin for automating gil transfers between two characters. It supports sending and receiving amounts larger than the per-trade limit by completing multiple trades in sequence.

**Maintainer:** Element40  
**Origin:** Fork of Gillionaire, originally developed by voidstar0 and contributors. Original authors retain copyright in their contributions.  
**License:** AGPL-3.0-or-later (see the upstream license and any retained source-file notices). Preserve the applicable license text and original notices when publishing this fork.

## Features

- Automated trade initiation, gil entry, acceptance, and confirmation on both characters.
- Transfers split into installments of at most **1,000,000 gil**.
- Receiver validates the incoming gil amount against the expected installment before pressing Trade.
- Receiver checks the sender's **first name** against the player targeted when receive mode was armed.
- Receiver attempts to cancel an active trade if the sender's name does not match or the target changes.
- Sender checks available gil before starting and before each installment; insufficient funds stop the transfer and generate an in-game message.
- Receiver automatically re-arms between installments and stops when the requested total has been received.

## Commands

| Command | Description |
| --- | --- |
| `/gilsend <amount>` | Send the specified total gil to your currently targeted player. |
| `/gilreceive <amount>` | Arm automatic receipt of the specified **total** gil from your currently targeted sender. |
| `/gilreceive off` | Disable receiver automation. |

Use positive whole-number amounts without commas (for example `2500000`). Both players should enter the **same total**, not the per-trade installment.

## How to use

1. Install and enable GilRouting on **both characters**.
2. On the receiving character, target the sending character and enter `/gilreceive 2500000`.
3. On the sending character, target the receiving character and enter `/gilsend 2500000`.
4. The plugin completes the required trades and reports progress in chat. The receiver automatically stops when the expected total is completed.
5. To stop receiving before completion, use `/gilreceive off`.

For **2,500,000 gil**, the sender currently sends the remainder first: **500,000**, then **1,000,000**, then **1,000,000**. The receiver validates each installment against that sequence. For 10,000,000 gil, ten 1,000,000-gil trades are expected.

**Safety notes:** First-name comparison is not a unique character-identity check; characters sharing the same first name can pass it. The plugin relies on game UI and trade-completion messages and may require updates after game/Dalamud changes. Test with small amounts and trusted characters before larger transfers. Automation may be subject to the game's terms of service.

## Build from source

The project uses the Dalamud .NET SDK 15 and ECommons. Build in a compatible development environment with .NET 10 and the Dalamud development dependencies available:

```bash
dotnet build GilRouting/GilRouting.csproj -c Release
```

The compiled DLL will be in the project's Release output directory.

### Load as a Dalamud development plugin

1. In game, enter `/xlsettings` and open **Experimental** settings.
2. Add the absolute path to the built `GilRouting.dll` under **Dev Plugin Locations**.
3. Open `/xlplugins`, find the **Installed Dev Plugins** section, and enable **GilRouting**.
4. Use the commands listed above.

Menu labels may vary with the Dalamud version.

## Troubleshooting

- **Receiver doesn't accept:** Verify both players targeted one another, the receiver is armed, and the incoming amount equals the expected installment. The receiver will not press Trade for a different amount.
- **Name mismatch:** The receiver checks the first name shown in the trade window against its target and attempts to cancel on mismatch. Re-target the correct sender and re-arm.
- **Insufficient gil:** The sender stops before opening a trade and reports its available balance and remaining requested amount in chat. Check the Dalamud log for diagnostics.
- **Trade interrupted or times out:** Re-arm and restart as appropriate after confirming how much gil was actually transferred. Do not assume an interrupted sequence completed.

## Attribution and licensing

GilRouting is a modified fork of **Gillionaire**. The original project and contributors retain attribution and copyright in their work. Changes to this fork include Dalamud API 15/.NET 10 compatibility, automated receiving, installment validation, sender-name validation, balance checks, and revised command names and messaging. The fork is maintained by **Element40**.

## License

GilRouting is licensed under the GNU Affero General Public
License v3.0 or later (AGPL-3.0-or-later).

See [LICENSE](LICENSE) for the complete license text.

GilRouting is a fork of Gillionaire by voidstar0.
Original copyright notices and attribution are retained.
