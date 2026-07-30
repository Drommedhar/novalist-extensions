namespace Novalist.Extensions.Toolkit.Services;

/// <summary>One culture's names.</summary>
public sealed class NameCulture
{
    public required IReadOnlyList<string> Feminine { get; init; }
    public required IReadOnlyList<string> Masculine { get; init; }

    /// <summary>Family names. Empty for a culture that does not use them.</summary>
    public required IReadOnlyList<string> Surnames { get; init; }
}

/// <summary>
/// The bundled per-culture name lists.
///
/// Small on purpose. These are not census data - they are training material for
/// a Markov chain, and thirty or forty names carry a culture's sound as well as
/// three thousand do. Bundling a hundred thousand names would grow the extension
/// by megabytes to make the generator no better.
///
/// The cultures are chosen for the sounds they cover rather than for any claim
/// to completeness: a writer wants "something that sounds Norse", and the label
/// is a description of the phonology, not of a people.
/// </summary>
internal static class NameData
{
    internal static readonly IReadOnlyDictionary<string, NameCulture> Cultures =
        new Dictionary<string, NameCulture>(StringComparer.OrdinalIgnoreCase)
        {
            ["english"] = new NameCulture
            {
                Feminine =
                [
                    "Alice", "Beatrice", "Clara", "Dorothy", "Edith", "Florence", "Grace",
                    "Harriet", "Imogen", "Jane", "Katherine", "Lydia", "Margaret", "Nora",
                    "Olive", "Prudence", "Rose", "Susanna", "Thea", "Verity", "Winifred"
                ],
                Masculine =
                [
                    "Albert", "Bertram", "Charles", "Duncan", "Edmund", "Francis", "George",
                    "Henry", "Isaac", "James", "Lawrence", "Martin", "Nicholas", "Oliver",
                    "Peter", "Richard", "Samuel", "Thomas", "Walter", "William"
                ],
                Surnames =
                [
                    "Ashworth", "Blackwood", "Carrow", "Dunmore", "Emberly", "Fairbairn",
                    "Garrick", "Hollis", "Ingham", "Larkin", "Mowbray", "Netherby",
                    "Ormsby", "Pemberton", "Rookwood", "Sandhurst", "Thornbury", "Whitlock"
                ]
            },
            ["germanic"] = new NameCulture
            {
                Feminine =
                [
                    "Adelheid", "Brunhild", "Cordula", "Dietlinde", "Elfriede", "Frieda",
                    "Gerlinde", "Hedwig", "Irmgard", "Kunigunde", "Liesel", "Mechthild",
                    "Ortrud", "Roswitha", "Sieglinde", "Traudel", "Ulrike", "Waltraud"
                ],
                Masculine =
                [
                    "Adalbert", "Bernhard", "Dietrich", "Eckhart", "Friedrich", "Gunther",
                    "Hartmut", "Konrad", "Ludwig", "Manfred", "Norbert", "Otto", "Reinhard",
                    "Siegfried", "Ulrich", "Volker", "Wilhelm", "Wolfram"
                ],
                Surnames =
                [
                    "Adlersberg", "Brandhorst", "Dammerau", "Eichenwald", "Falkenstein",
                    "Grunewald", "Hohenfels", "Kirchhoff", "Lindenmayer", "Morgenstern",
                    "Nachtigall", "Rosenkranz", "Steinbach", "Thalheim", "Waldschmidt"
                ]
            },
            ["norse"] = new NameCulture
            {
                Feminine =
                [
                    "Aslaug", "Bergljot", "Dagny", "Eir", "Freydis", "Gudrun", "Halldis",
                    "Ingrid", "Jorunn", "Kolfinna", "Ragnhild", "Sigrun", "Solveig",
                    "Thordis", "Thurid", "Unnr", "Yrsa"
                ],
                Masculine =
                [
                    "Arnfinn", "Bjorn", "Eirik", "Fenrir", "Gunnar", "Halvard", "Ivar",
                    "Kjartan", "Leifr", "Njall", "Ottar", "Ragnar", "Sigurd", "Snorri",
                    "Thorvald", "Ulfr", "Vidar"
                ],
                Surnames =
                [
                    "Bjornsson", "Eiriksdottir", "Gunnarsson", "Halvardsson", "Ivarsdottir",
                    "Ragnarsson", "Sigurdsdottir", "Thorvaldsson", "Ulfsson"
                ]
            },
            ["slavic"] = new NameCulture
            {
                Feminine =
                [
                    "Bogumila", "Danica", "Jadwiga", "Katarzyna", "Ludmila", "Milena",
                    "Nadezhda", "Olesya", "Radmila", "Stanislava", "Svetlana", "Vesna",
                    "Wanda", "Zdenka", "Zoryana"
                ],
                Masculine =
                [
                    "Boguslav", "Casimir", "Dragomir", "Jaroslav", "Kazimierz", "Lubomir",
                    "Miroslav", "Nikodem", "Radoslav", "Stanislav", "Tomasz", "Vladek",
                    "Wojciech", "Zbigniew"
                ],
                Surnames =
                [
                    "Baranowski", "Cieslak", "Dvorak", "Jablonski", "Kowalczyk", "Novotny",
                    "Petrovich", "Sokolov", "Vranek", "Zielinski"
                ]
            },
            ["romance"] = new NameCulture
            {
                Feminine =
                [
                    "Adriana", "Beatriz", "Chiara", "Elena", "Fiammetta", "Giulia",
                    "Isabela", "Lucrezia", "Mariana", "Ninetta", "Ottavia", "Rosalia",
                    "Serafina", "Valentina", "Ximena"
                ],
                Masculine =
                [
                    "Alessandro", "Bartolomeo", "Cesare", "Diego", "Emilio", "Fabrizio",
                    "Gaspare", "Ignacio", "Lorenzo", "Matteo", "Nicolau", "Rodrigo",
                    "Salvatore", "Tiberio", "Vicente"
                ],
                Surnames =
                [
                    "Aguilar", "Barbieri", "Carvalho", "Delacroix", "Esposito", "Fontana",
                    "Guerrero", "Lombardi", "Montalvo", "Peralta", "Rossellini", "Salazar",
                    "Trevisan", "Vasconcelos"
                ]
            },
            ["celtic"] = new NameCulture
            {
                Feminine =
                [
                    "Aoife", "Blodwen", "Bronagh", "Ceridwen", "Deirdre", "Eithne",
                    "Fionnuala", "Gwenllian", "Isolde", "Maeve", "Niamh", "Orlaith",
                    "Rhiannon", "Sorcha", "Una"
                ],
                Masculine =
                [
                    "Aodhan", "Bran", "Cadwallon", "Cormac", "Diarmuid", "Eoghan",
                    "Fionnbharr", "Gwilym", "Lorcan", "Maddox", "Niall", "Oisin",
                    "Ruaidhri", "Taliesin", "Conall"
                ],
                Surnames =
                [
                    "Brannigan", "Caolan", "Donnelly", "Fahey", "Gallagher", "Kavanagh",
                    "Llewellyn", "Maguire", "Ó Ceallaigh", "Pendry", "Quilligan", "Rafferty"
                ]
            },
            ["arabic"] = new NameCulture
            {
                Feminine =
                [
                    "Amira", "Basma", "Dalia", "Farida", "Hanan", "Jamila", "Khadija",
                    "Layla", "Maysoon", "Nadira", "Rania", "Saliha", "Thurayya", "Yasmin",
                    "Zaynab"
                ],
                Masculine =
                [
                    "Abbas", "Bashir", "Faisal", "Ghassan", "Hakim", "Idris", "Jamal",
                    "Kareem", "Mahmoud", "Nabil", "Rashid", "Sulayman", "Tariq", "Yusuf",
                    "Zahir"
                ],
                Surnames =
                [
                    "al-Ansari", "al-Farsi", "al-Hashimi", "al-Jabri", "al-Khalidi",
                    "al-Masri", "al-Najjar", "al-Qadir", "al-Rashidi", "al-Sayegh"
                ]
            },
            ["japanese"] = new NameCulture
            {
                Feminine =
                [
                    "Akiko", "Chiyo", "Emiko", "Fumiko", "Haruna", "Kaede", "Michiyo",
                    "Nanako", "Rinako", "Sachiko", "Tomoe", "Yukari", "Yuzuki"
                ],
                Masculine =
                [
                    "Akihiro", "Daisuke", "Hayato", "Isamu", "Kenjiro", "Masaru",
                    "Noboru", "Ryunosuke", "Shinobu", "Tatsuya", "Yasuhiro", "Yoshirou"
                ],
                Surnames =
                [
                    "Akiyama", "Fujimori", "Hasegawa", "Ishikawa", "Kurosawa", "Matsumoto",
                    "Nakamura", "Okabayashi", "Shimizu", "Tachibana", "Yamashiro"
                ]
            }
        };
}
