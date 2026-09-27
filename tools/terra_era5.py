"""Download the ERA5 monthly means Terra's sea state is baked from into Data/Sources/era5.

One NetCDF per variable: every month of 1991-2020 (the WMO normal) on the 0.5 degree wave grid. Needs a Copernicus
CDS account whose token is in ~/.cdsapirc, and the dataset licence accepted once on its web page. Each file is
skipped once present, so a rerun resumes.
"""

import os
import sys

import cdsapi

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "Data", "Sources", "era5")

VARIABLES = {
    "u10": "10m_u_component_of_wind",
    "v10": "10m_v_component_of_wind",
    "si10": "10m_wind_speed",
    "shww": "significant_height_of_wind_waves",
    "mpww": "mean_period_of_wind_waves",
    "mdww": "mean_direction_of_wind_waves",
    "shts": "significant_height_of_total_swell",
    "mpts": "mean_period_of_total_swell",
    "mdts": "mean_direction_of_total_swell",
    "siconc": "sea_ice_cover",
}


def main():

    os.makedirs(OUT, exist_ok=True)
    client = cdsapi.Client()

    for short, name in VARIABLES.items():

        path = os.path.join(OUT, f"{short}.nc")

        if os.path.exists(path):

            continue

        print(f"requesting {name}", flush=True)
        client.retrieve("reanalysis-era5-single-levels-monthly-means", {
            "product_type": ["monthly_averaged_reanalysis"],
            "variable": [name],
            "year": [str(y) for y in range(1991, 2021)],
            "month": [f"{m:02d}" for m in range(1, 13)],
            "time": ["00:00"],
            "grid": [0.5, 0.5],
            "data_format": "netcdf",
            "download_format": "unarchived",
        }, path + ".part")
        os.replace(path + ".part", path)


if __name__ == "__main__":

    sys.exit(main())
