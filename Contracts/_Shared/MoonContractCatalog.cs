using System.Collections.Generic;

namespace Y4NGZCompany.Contracts._Shared
{
    internal static class MoonContractCatalog
    {
        private static readonly Dictionary<MoonContractType, MoonContractDefinition> Definitions =
            new Dictionary<MoonContractType, MoonContractDefinition>
            {
                {
                    MoonContractType.None,
                    new MoonContractDefinition(
                        "Salvage",
                        "Salvage",
                        "SALVAGE",
                        "Residual assets remain on-site. Recover all salvage and log it aboard the ship.",
                        "Recover all scrap on site and carry it aboard the ship. Payment is assessed on delivered value.",
                        "Recover valuable scrap")
                },
                {
                    MoonContractType.Whistleblower,
                    new MoonContractDefinition(
                        "Whistleblower",
                        "Whistle",
                        "WHISTLEBLOWER",
                        "An employee has breached disclosure policy. Locate the witness and close the leak before he exits the facility.",
                        "The witness runs for a facility exit. Find and neutralize him before he escapes, or the contract voids.",
                        "Find and neutralize witness",
                        "  \\  |  /\n -- [X] --\n  /  |  \\")
                },
                {
                    MoonContractType.BlackoutAudit,
                    new MoonContractDefinition(
                        "Blackout Audit",
                        "Blackout",
                        "BLACKOUT AUDIT",
                        "This facility's power grid is out of compliance. Locate every breaker box and restore output to specification.",
                        "The facility spawns unpowered. Each breaker box you reset restores power to its own wing; reset every one for full output.",
                        "Restore facility power",
                        "  [POWER]\n     |\n   --o\n     |")
                },
                {
                    MoonContractType.ShadowRaid,
                    new MoonContractDefinition(
                        "Shadow Raid",
                        "Raid",
                        "SHADOW RAID",
                        "A competitor's sealed cache obstructs Company interests. Deploy the raid drill and recover the hard drive inside.",
                        "Carry the raid drill to the sealed container, attach it, survive the loud drill, then take the hard drive.",
                        "Recover the sealed hard drive",
                        "  [####]\n  | VA |\n ==    ==")
                },
                {
                    MoonContractType.ContainmentBreach,
                    new MoonContractDefinition(
                        "Containment Breach",
                        "Containment",
                        "CONTAINMENT BREACH",
                        "This site is scheduled for a hostile-response stress test. Remain inside the facility for the duration of the assessment.",
                        "Stay inside the facility and survive every scripted enemy wave. Leaving before the last wave voids it.",
                        "Survive hostile response waves",
                        "   /\\/\\\n  (o  o)\n  /|##|\\\n   /  \\")
                },
                {
                    MoonContractType.Survey,
                    new MoonContractDefinition(
                        "Survey",
                        "Survey",
                        "SURVEY",
                        "Interior zones require assessment. Deploy survey beacons at the designated positions. Retrieval is not required.",
                        "Carry survey beacons to the marked interior positions and deploy each one. No retrieval is required.",
                        "Deploy survey beacons",
                        "  .------.\n  |SCAN>>|\n  |  03  |\n  '------'")
                },
                {
                    MoonContractType.Defuse,
                    new MoonContractDefinition(
                        "Defuse",
                        "Defusal",
                        "DEFUSE",
                        "An armed device threatens Company property. Locate the case; Field Support holds the wire sequence and disarm code.",
                        "An armed case is counting down. Find it, read the wire sequence and code off the terminal, then disarm.",
                        "Disarm the armed briefcase",
                        "  .--------.\n  | ARMED  |\n  | 1 2 3 |\n  '--------'")
                },
                {
                    MoonContractType.Payload,
                    new MoonContractDefinition(
                        "Payload Haul",
                        "Payload",
                        "PAYLOAD HAUL",
                        "A containment cart awaits transfer. Route it through the main entrance and secure it to the ship's hull.",
                        "Push the containment cart out the main entrance and secure it at the ship. Pushing it slows you down.",
                        "Deliver sealed containment cargo",
                        "  .------.\n  |[####]|\n  | (o)  |\n  '-oo--oo'")
                },
                {
                    MoonContractType.WasteDisposal,
                    new MoonContractDefinition(
                        "Waste Disposal",
                        "Disposal",
                        "WASTE DISPOSAL",
                        "Assigned assets are scheduled for thermal decommissioning. Feed the incinerator and certify the burn.",
                        "Find the incinerator, feed it scrap up to the target value, and run the burn. Repair any malfunctions.",
                        "Incinerate assigned scrap",
                        "   /^^\\\n  |FIRE|\n  |____|\n  /|  |\\")
                },
                {
                    MoonContractType.PestControl,
                    new MoonContractDefinition(
                        "Pest Control",
                        "Pest",
                        "PEST CONTROL",
                        "A live specimen has been flagged for acquisition. Rig the Company trap, lure it onto the platform, and contain it.",
                        "Place the cage trap, lure a specimen onto the platform, drop the cage, and leave the moon with it.",
                        "Trap and contain a specimen",
                        "   ______\n  /|    |\\\n /_|____|_\\\n   | () |")
                },
                // #803: the Lategame Upgrades-backed directives. The mechanics are LGU's; the
                // briefing, the rotation slot and the payout framing are Contracted's.
                {
                    MoonContractType.LguDataRetrieval,
                    new MoonContractDefinition(
                        "Data Retrieval",
                        "Data",
                        "DATA RETRIEVAL",
                        "A survey terminal was abandoned deep in the facility. Recover its records before the site is written off.",
                        "Find the laptop, read its IP, run 'bruteforce <ip>' on the ship terminal, then log in and pull survey.db. A floppy disk is left behind.",
                        "Recover the survey database",
                        "  .------.\n  |>_    |\n  '------'\n  [======]")
                },
                {
                    MoonContractType.LguExtermination,
                    new MoonContractDefinition(
                        "Extermination",
                        "Exterminate",
                        "EXTERMINATION",
                        "An infestation has taken the facility. Clear the swarm and destroy the nest it is defending.",
                        "A hoarding bug nest sits under guard. Kill every bug around it, then destroy the nest itself. The eggs are salvage once the site is clear.",
                        "Destroy the hoarding bug nest",
                        "   .-''-.\n  ( o  o )\n   '-..-'\n  /_||||_\\")
                },
                {
                    MoonContractType.LguExtraction,
                    new MoonContractDefinition(
                        "Extraction",
                        "Extract",
                        "EXTRACTION",
                        "A contracted scavenger is down inside and still insured. Stabilize him and bring him back aboard.",
                        "Find the downed scavenger, treat him with a medkit dropped on the moon, then carry him to the ship. He is the payout, so deliver him intact.",
                        "Rescue the downed scavenger",
                        "   .--.\n  ( ++ )\n  /|__|\\\n  _|  |_")
                },
                {
                    MoonContractType.LguExorcism,
                    new MoonContractDefinition(
                        "Exorcism",
                        "Exorcism",
                        "EXORCISM",
                        "Unlicensed ritual activity is interfering with operations. Complete the rite and remove the occupant.",
                        // #804: three items, not five, and the order is not enforced - LGU's
                        // altar only checks that the item is on the demon's list.
                        "Find the pentagram, run 'demon <name>' on the terminal for the item list, then place the three ritual items. A wrong placement calls something worse.",
                        "Complete the banishing ritual",
                        "   /\\_/\\\n  <  *  >\n   \\/ \\/\n  ---------")
                },
                {
                    MoonContractType.LguDefusal,
                    new MoonContractDefinition(
                        "Defusal",
                        "Defusal",
                        "DEFUSAL",
                        "An armed device is sitting on Company property. Read the wiring off the terminal and shut it down.",
                        "Find the device, run 'lookup <serial>' on the terminal for the wire order, then cut red, blue and green in that order. A wrong cut detonates it.",
                        "Disarm the armed device",
                        "  .--------.\n  | * ARM  |\n  | //// |\n  '--------'")
                }
            };

        internal static MoonContractDefinition Get(MoonContractType type)
        {
            if (Definitions.TryGetValue(type, out var definition))
                return definition;
            return Definitions[MoonContractType.None];
        }
    }
}
