using System.Collections.Generic;
using System.Linq;
using Godot;

public sealed record SteamCosmeticDefinition(int ItemDefId, string Name, string Kind, string AssetPath, string UiAssetPath)
{
    public string AccessoryId => $"steam:{ItemDefId}";
}

// IDs and paths match SteamInventory/itemdefs.json. This catalog contains no ownership or balance.
public static class SteamCosmeticCatalog
{
	private static readonly Dictionary<string, Texture2D> CroppedTextures = new();
    public static IReadOnlyList<SteamCosmeticDefinition> All { get; } = new SteamCosmeticDefinition[]
    {
        new(2000, "Angel Wing", "accessory", "res://Art/Items/AngelWing.png", "res://Art/UI/Items/AngelWing.png"),
        new(2001, "Ball", "accessory", "res://Art/Items/Ball.png", "res://Art/UI/Items/Ball.png"),
        new(2002, "Bandana 1", "accessory", "res://Art/Items/Bandana1.png", "res://Art/UI/Items/Bandana1.png"),
        new(2003, "Bone", "accessory", "res://Art/Items/Bone.png", "res://Art/UI/Items/Bone.png"),
        new(2004, "Bow 1", "accessory", "res://Art/Items/Bow1.png", "res://Art/UI/Items/Bow1.png"),
        new(2005, "Chef Hat", "accessory", "res://Art/Items/ChefHat.png", "res://Art/UI/Items/ChefHat.png"),
        new(2006, "Circle Charm", "accessory", "res://Art/Items/CircleCharm.png", "res://Art/UI/Items/CircleCharm.png"),
        new(2007, "Collar 1", "accessory", "res://Art/Items/Collar1.png", "res://Art/UI/Items/Collar1.png"),
        new(2008, "Collar Clip", "accessory", "res://Art/Items/CollarClip.png", "res://Art/UI/Items/CollarClip.png"),
        new(2009, "Crown 1", "accessory", "res://Art/Items/Crown1.png", "res://Art/UI/Items/Crown1.png"),
        new(2010, "Diamond Charm", "accessory", "res://Art/Items/DiamondCharm.png", "res://Art/UI/Items/DiamondCharm.png"),
        new(2011, "Dragon Wing", "accessory", "res://Art/Items/DragonWing.png", "res://Art/UI/Items/DragonWing.png"),
        new(2012, "Extra Eye (Half Open) 1", "accessory", "res://Art/Items/ExtraEyeHalf1.png", "res://Art/UI/Items/ExtraEyeHalf1.png"),
        new(2013, "Extra Eye (Half Open) 2", "accessory", "res://Art/Items/ExtraEyeHalf2.png", "res://Art/UI/Items/ExtraEyeHalf2.png"),
        new(2014, "Extra Eye (Open) 1", "accessory", "res://Art/Items/ExtraEyeOpen1.png", "res://Art/UI/Items/ExtraEyeOpen1.png"),
        new(2015, "Extra Eye (Open) 2", "accessory", "res://Art/Items/ExtraEyeOpen2.png", "res://Art/UI/Items/ExtraEyeOpen2.png"),
        new(2016, "Eyepatch", "accessory", "res://Art/Items/Eyepatch.png", "res://Art/UI/Items/Eyepatch.png"),
        new(2017, "Fairy Wing", "accessory", "res://Art/Items/FairyWing.png", "res://Art/UI/Items/FairyWing.png"),
        new(2018, "Googly Eye", "accessory", "res://Art/Items/GooglyEye.png", "res://Art/UI/Items/GooglyEye.png"),
        new(2019, "Halo", "accessory", "res://Art/Items/Halo.png", "res://Art/UI/Items/Halo.png"),
        new(2020, "Heart Charm", "accessory", "res://Art/Items/HeartCharm.png", "res://Art/UI/Items/HeartCharm.png"),
        new(2021, "Heart Glasses", "accessory", "res://Art/Items/HeartGlasses.png", "res://Art/UI/Items/HeartGlasses.png"),
        new(2022, "Horn 1", "accessory", "res://Art/Items/Horn1.png", "res://Art/UI/Items/Horn1.png"),
        new(2023, "Hot Dog Bun", "accessory", "res://Art/Items/HotDogBun.png", "res://Art/UI/Items/HotDogBun.png"),
        new(2024, "Jester Hat 1", "accessory", "res://Art/Items/JesterHat1.png", "res://Art/UI/Items/JesterHat1.png"),
        new(2025, "Jester Hat 2", "accessory", "res://Art/Items/JesterHat2.png", "res://Art/UI/Items/JesterHat2.png"),
        new(2026, "Round Glasses", "accessory", "res://Art/Items/RoundGlasses.png", "res://Art/UI/Items/RoundGlasses.png"),
        new(2027, "Rubber Duck", "accessory", "res://Art/Items/RubberDuck.png", "res://Art/UI/Items/RubberDuck.png"),
        new(2028, "Safety Glasses", "accessory", "res://Art/Items/SafetyGlasses.png", "res://Art/UI/Items/SafetyGlasses.png"),
        new(2029, "Sauce", "accessory", "res://Art/Items/Sauce.png", "res://Art/UI/Items/Sauce.png"),
        new(2030, "Scarf", "accessory", "res://Art/Items/Scarf.png", "res://Art/UI/Items/Scarf.png"),
        new(2031, "Flower 1", "accessory", "res://Art/Items/SFlower1.png", "res://Art/UI/Items/SFlower1.png"),
        new(2032, "Flower 2", "accessory", "res://Art/Items/SFlower2.png", "res://Art/UI/Items/SFlower2.png"),
        new(2033, "Sock", "accessory", "res://Art/Items/Sock.png", "res://Art/UI/Items/Sock.png"),
        new(2034, "Square Charm", "accessory", "res://Art/Items/SquareCharm.png", "res://Art/UI/Items/SquareCharm.png"),
        new(2035, "Straw Hat 1", "accessory", "res://Art/Items/StrawHat1.png", "res://Art/UI/Items/StrawHat1.png"),
        new(2036, "Straw Hat 2", "accessory", "res://Art/Items/StrawHat2.png", "res://Art/UI/Items/StrawHat2.png"),
        new(2037, "Tiara", "accessory", "res://Art/Items/Tiara.png", "res://Art/UI/Items/Tiara.png"),
        new(2038, "Tie", "accessory", "res://Art/Items/Tie.png", "res://Art/UI/Items/Tie.png"),
        new(2039, "Top Hat 1", "accessory", "res://Art/Items/TopHat1.png", "res://Art/UI/Items/TopHat1.png"),
        new(2040, "Top Hat 2", "accessory", "res://Art/Items/TopHat2.png", "res://Art/UI/Items/TopHat2.png"),
        new(2041, "Triangle Glasses", "accessory", "res://Art/Items/TriangleGlasses.png", "res://Art/UI/Items/TriangleGlasses.png"),
        new(2042, "Tutu", "accessory", "res://Art/Items/Tutu.png", "res://Art/UI/Items/Tutu.png"),
        new(3000, "Australian Shepherd 1", "dog", "res://Art/Dogs/AustralianShepard1.png", "res://Art/UI/Dogs/AustralianShepard1.png"),
        new(3001, "Australian Shepherd 2", "dog", "res://Art/Dogs/AustralianShepard2.png", "res://Art/UI/Dogs/AustralianShepard2.png"),
        new(3002, "Basset Hound 1", "dog", "res://Art/Dogs/Bassethound1.png", "res://Art/UI/Dogs/Bassethound1.png"),
        new(3003, "Basset Hound 2", "dog", "res://Art/Dogs/Bassethound2.png", "res://Art/UI/Dogs/Bassethound2.png"),
        new(3004, "Bloodhound 1", "dog", "res://Art/Dogs/Bloodhound1.png", "res://Art/UI/Dogs/Bloodhound1.png"),
        new(3005, "Bloodhound 2", "dog", "res://Art/Dogs/Bloodhound2.png", "res://Art/UI/Dogs/Bloodhound2.png"),
        new(3006, "Border Collie 1", "dog", "res://Art/Dogs/BorderCollie1.png", "res://Art/UI/Dogs/BorderCollie1.png"),
        new(3007, "Border Collie 2", "dog", "res://Art/Dogs/BorderCollie2.png", "res://Art/UI/Dogs/BorderCollie2.png"),
        new(3008, "Borzoi 1", "dog", "res://Art/Dogs/Borzoi1.png", "res://Art/UI/Dogs/Borzoi1.png"),
        new(3009, "Borzoi 2", "dog", "res://Art/Dogs/Borzoi2.png", "res://Art/UI/Dogs/Borzoi2.png"),
        new(3010, "Boxer 1", "dog", "res://Art/Dogs/Boxer1.png", "res://Art/UI/Dogs/Boxer1.png"),
        new(3011, "Boxer 2", "dog", "res://Art/Dogs/Boxer2.png", "res://Art/UI/Dogs/Boxer2.png"),
        new(3012, "Bulldog 1", "dog", "res://Art/Dogs/Bulldog1.png", "res://Art/UI/Dogs/Bulldog1.png"),
        new(3013, "Bulldog 2", "dog", "res://Art/Dogs/Bulldog2.png", "res://Art/UI/Dogs/Bulldog2.png"),
        new(3014, "Chihuahua 1", "dog", "res://Art/Dogs/Chihuahua1.png", "res://Art/UI/Dogs/Chihuahua1.png"),
        new(3015, "Chihuahua 2", "dog", "res://Art/Dogs/Chihuahua2.png", "res://Art/UI/Dogs/Chihuahua2.png"),
        new(3016, "Cocker Spaniel 1", "dog", "res://Art/Dogs/CockerSpaniel1.png", "res://Art/UI/Dogs/CockerSpaniel1.png"),
        new(3017, "Cocker Spaniel 2", "dog", "res://Art/Dogs/CockerSpaniel2.png", "res://Art/UI/Dogs/CockerSpaniel2.png"),
        new(3018, "Corgi 1", "dog", "res://Art/Dogs/Corgi1.png", "res://Art/UI/Dogs/Corgi1.png"),
        new(3019, "Corgi 2", "dog", "res://Art/Dogs/Corgi2.png", "res://Art/UI/Dogs/Corgi2.png"),
        new(3020, "English Sheepdog 1", "dog", "res://Art/Dogs/EnglishSheepdog1.png", "res://Art/UI/Dogs/EnglishSheepdog1.png"),
        new(3021, "English Sheepdog 2", "dog", "res://Art/Dogs/EnglishSheepdog2.png", "res://Art/UI/Dogs/EnglishSheepdog2.png"),
        new(3022, "Great Dane 1", "dog", "res://Art/Dogs/GreatDane1.png", "res://Art/UI/Dogs/GreatDane1.png"),
        new(3023, "Great Dane 2", "dog", "res://Art/Dogs/GreatDane2.png", "res://Art/UI/Dogs/GreatDane2.png"),
        new(3024, "Great Pyrenees 1", "dog", "res://Art/Dogs/GreatPyranees1.png", "res://Art/UI/Dogs/GreatPyranees1.png"),
        new(3025, "Great Pyrenees 2", "dog", "res://Art/Dogs/GreatPyranees2.png", "res://Art/UI/Dogs/GreatPyranees2.png"),
        new(3026, "Greyhound 1", "dog", "res://Art/Dogs/Greyhound1.png", "res://Art/UI/Dogs/Greyhound1.png"),
        new(3027, "Greyhound 2", "dog", "res://Art/Dogs/Greyhound2.png", "res://Art/UI/Dogs/Greyhound2.png"),
        new(3028, "Husky 1", "dog", "res://Art/Dogs/Husky1.png", "res://Art/UI/Dogs/Husky1.png"),
        new(3029, "Husky 2", "dog", "res://Art/Dogs/Husky2.png", "res://Art/UI/Dogs/Husky2.png"),
        new(3030, "Labrador Retriever 1", "dog", "res://Art/Dogs/Lab1.png", "res://Art/UI/Dogs/Lab1.png"),
        new(3031, "Labrador Retriever 2", "dog", "res://Art/Dogs/Lab2.png", "res://Art/UI/Dogs/Lab2.png"),
        new(3032, "Labrador Retriever 3", "dog", "res://Art/Dogs/Lab3.png", "res://Art/UI/Dogs/Lab3.png"),
        new(3033, "Pomeranian 1", "dog", "res://Art/Dogs/Pomeranian1.png", "res://Art/UI/Dogs/Pomeranian1.png"),
        new(3034, "Pomeranian 2", "dog", "res://Art/Dogs/Pomeranian2.png", "res://Art/UI/Dogs/Pomeranian2.png"),
        new(3035, "Poodle 1", "dog", "res://Art/Dogs/Poodle1.png", "res://Art/UI/Dogs/Poodle1.png"),
        new(3036, "Poodle 2", "dog", "res://Art/Dogs/Poodle2.png", "res://Art/UI/Dogs/Poodle2.png"),
        new(3037, "Pug 1", "dog", "res://Art/Dogs/Pug1.png", "res://Art/UI/Dogs/Pug1.png"),
        new(3038, "Pug 2", "dog", "res://Art/Dogs/Pug2.png", "res://Art/UI/Dogs/Pug2.png"),
        new(3039, "Rat Terrier 1", "dog", "res://Art/Dogs/RatTerrier1.png", "res://Art/UI/Dogs/RatTerrier1.png"),
        new(3040, "Rat Terrier 2", "dog", "res://Art/Dogs/RatTerrier2.png", "res://Art/UI/Dogs/RatTerrier2.png"),
        new(3041, "Shih Tzu 1", "dog", "res://Art/Dogs/ShihTzu1.png", "res://Art/UI/Dogs/ShihTzu1.png"),
        new(3042, "Shih Tzu 2", "dog", "res://Art/Dogs/ShihTzu2.png", "res://Art/UI/Dogs/ShihTzu2.png"),
        new(3043, "Saint Bernard 1", "dog", "res://Art/Dogs/StBernard1.png", "res://Art/UI/Dogs/StBernard1.png"),
        new(3044, "Saint Bernard 2", "dog", "res://Art/Dogs/StBernard2.png", "res://Art/UI/Dogs/StBernard2.png"),
    };

    public static SteamCosmeticDefinition? Find(int itemDefId) => All.FirstOrDefault(item => item.ItemDefId == itemDefId);

    public static Texture2D? CroppedTexture(string path)
    {
		if (CroppedTextures.TryGetValue(path, out var cached)) return cached;
        var texture = ResourceLoader.Load<Texture2D>(path);
        if (texture == null) return null;
        var bounds = AccessoryWardrobe.GetVisibleBounds(texture);
        var cropped = bounds.Size == Vector2I.Zero ? texture
            : new AtlasTexture { Atlas = texture, Region = bounds, FilterClip = true };
		CroppedTextures[path] = cropped;
		return cropped;
    }
}

