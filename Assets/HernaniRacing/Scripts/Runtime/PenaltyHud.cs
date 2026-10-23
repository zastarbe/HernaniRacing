using UnityEngine;

namespace HernaniRacing
{
    /// <summary>Aviso de sanción por atropello: "¡ATROPELLO!" y la cuenta atrás 2,000 → 0,000.</summary>
    public static class PenaltyHud
    {
        static GUIStyle title, counter, sub;

        public static string Format(float secs)
        {
            // coma decimal, como en España: 2,000 1,537 ... 0,000
            return Mathf.Max(0f, secs).ToString("0.000", System.Globalization.CultureInfo.InvariantCulture).Replace('.', ',');
        }

        public static void Draw(float W, float H, CarController car)
        {
            if (car == null || car.PenaltyLeft <= 0f) return;
            if (title == null)
            {
                title = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontSize = 50, fontStyle = FontStyle.Bold };
                title.normal.textColor = new Color(1f, 0.32f, 0.25f);
                counter = new GUIStyle(title) { fontSize = 92 };
                counter.normal.textColor = Color.white;
                sub = new GUIStyle(title) { fontSize = 22, fontStyle = FontStyle.Normal };
                sub.normal.textColor = new Color(1f, 0.9f, 0.85f);
            }
            float w = 560, h = 230;
            Rect r = new Rect(W / 2 - w / 2, H * 0.30f - h / 2, w, h);
            var old = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.62f);
            GUI.DrawTexture(r, Texture2D.whiteTexture);
            GUI.color = old;
            GUI.Label(new Rect(r.x, r.y + 10, w, 60), "¡ATROPELLO!", title);
            GUI.Label(new Rect(r.x, r.y + 66, w, 110), Format(car.PenaltyLeft), counter);
            GUI.Label(new Rect(r.x, r.y + 178, w, 36), car.PenaltyCounting ? "Sanción: parado 2 segundos" : "Frenando...", sub);
        }
    }
}
